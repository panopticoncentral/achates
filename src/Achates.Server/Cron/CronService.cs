using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Events;
using Achates.Server.Tools;
using Achates.Transports;

namespace Achates.Server.Cron;

/// <summary>
/// Background service that manages a timer loop, finds due cron jobs, and executes them.
/// Created manually by GatewayService after the Gateway is ready.
/// </summary>
public sealed class CronService : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, (CronStore Store, AgentDefinition Agent)> _agents;
    private readonly IReadOnlyList<ChannelBinding> _bindings;
    private readonly ILogger<CronService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _poke = new(0);
    private Task? _loopTask;

    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(2);

    public CronService(
        IReadOnlyDictionary<string, (CronStore Store, AgentDefinition Agent)> agents,
        IReadOnlyList<ChannelBinding> bindings,
        ILogger<CronService> logger)
    {
        _agents = agents;
        _bindings = bindings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _loopTask = RunLoopAsync(_cts.Token);
        _logger.LogInformation("Cron service started with {Count} agent(s)", _agents.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wake the timer loop early (called after job mutations via CronTool).
    /// </summary>
    public void Poke()
    {
        // Release only if the semaphore is at 0 (avoid stacking)
        if (_poke.CurrentCount == 0)
            _poke.Release();
    }

    /// <summary>
    /// Run a specific job immediately. Returns the result text.
    /// </summary>
    public async Task<string?> RunJobAsync(string agentName, string jobId, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentName, out var entry))
            return null;

        var jobs = await entry.Store.LoadAsync(ct);
        var job = jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) return null;

        return await ExecuteJobAsync(agentName, entry.Agent, entry.Store, job, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _poke.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Small delay to let the system settle at startup
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        // Initialize NextRunAt for any jobs that don't have one
        await InitializeJobStatesAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sleepDuration = await GetSleepDurationAsync(ct);
                await SleepUntilPokedOrTimeoutAsync(sleepDuration, ct);

                var dueJobs = await CollectDueJobsAsync(ct);
                foreach (var (agentName, agent, store, job) in dueJobs)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var result = await ExecuteJobAsync(agentName, agent, store, job, ct);
                        await UpdateJobStateAfterRunAsync(store, job, "ok", null, ct);
                        _logger.LogInformation("Cron job '{Name}' ({Id}) completed", job.Name, job.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await UpdateJobStateAfterRunAsync(store, job, "error", ex.Message, ct);
                        _logger.LogError(ex, "Cron job '{Name}' ({Id}) failed", job.Name, job.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cron loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task InitializeJobStatesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (agentName, (store, _)) in _agents)
        {
            var jobs = await store.LoadAsync(ct);
            foreach (var job in jobs.Where(j => j.Enabled && j.State.NextRunAt is null))
            {
                var next = CronScheduler.ComputeNextRun(job.Schedule, now);
                if (next is not null)
                {
                    await store.SaveJobStateAsync(job.Id, s => s.NextRunAt = next, ct);
                    _logger.LogDebug("Initialized job '{Name}' next run: {Next}", job.Name, next);
                }
                else
                {
                    await store.DisableAsync(job.Id, ct);
                    _logger.LogInformation("Disabled exhausted job '{Name}'", job.Name);
                }
            }
        }
    }

    private async Task<TimeSpan> GetSleepDurationAsync(CancellationToken ct)
    {
        DateTimeOffset? earliest = null;
        var now = DateTimeOffset.UtcNow;

        foreach (var (_, (store, _)) in _agents)
        {
            var jobs = await store.LoadAsync(ct);
            foreach (var job in jobs.Where(j => j.Enabled && j.State.NextRunAt is not null))
            {
                if (earliest is null || job.State.NextRunAt < earliest)
                    earliest = job.State.NextRunAt;
            }
        }

        if (earliest is null)
            return MaxSleep;

        var delay = earliest.Value - now;
        if (delay <= TimeSpan.Zero)
            return MinSleep;

        return delay > MaxSleep ? MaxSleep : delay;
    }

    private async Task SleepUntilPokedOrTimeoutAsync(TimeSpan duration, CancellationToken ct)
    {
        // Wait for either: poke signal, timeout, or cancellation
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(duration, delayCts.Token);
        var pokeTask = _poke.WaitAsync(ct);

        await Task.WhenAny(delayTask, pokeTask);

        // Cancel the delay if poke arrived first
        await delayCts.CancelAsync();
    }

    private async Task<List<(string AgentName, AgentDefinition Agent, CronStore Store, CronJob Job)>> CollectDueJobsAsync(CancellationToken ct)
    {
        var due = new List<(string, AgentDefinition, CronStore, CronJob)>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (agentName, (store, agent)) in _agents)
        {
            var jobs = await store.LoadAsync(ct);
            foreach (var job in jobs.Where(j => j.Enabled && j.State.NextRunAt <= now))
            {
                due.Add((agentName, agent, store, job));
            }
        }

        return due;
    }

    private async Task<string?> ExecuteJobAsync(
        string agentName, AgentDefinition agentDef, CronStore store, CronJob job, CancellationToken ct)
    {
        _logger.LogInformation("Executing cron job '{Name}' ({Id}) for agent '{Agent}'",
            job.Name, job.Id, agentName);

        // Build tool list: shared tools + per-agent tools, excluding CronTool
        var tools = BuildJobTools(agentDef);

        var agent = new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = agentDef.SystemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
        });

        var stream = agent.PromptAsync(
            $"[Scheduled task: {job.Name}]\n\n{job.Message}");

        var responseText = "";

        await foreach (var evt in stream.WithCancellation(ct))
        {
            switch (evt)
            {
                case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                    responseText += delta.Delta;
                    break;

                case MessageEndEvent { Message: Agent.Messages.AssistantMessage assistantMsg }:
                    // Record cost
                    if (agentDef.CostLedger is { } costLedger)
                    {
                        _ = costLedger.AppendAsync(new CostEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Model = assistantMsg.Model,
                            Channel = "cron",
                            Peer = job.Id,
                            InputTokens = assistantMsg.Usage.Input,
                            OutputTokens = assistantMsg.Usage.Output,
                            CacheReadTokens = assistantMsg.Usage.CacheRead,
                            CacheWriteTokens = assistantMsg.Usage.CacheWrite,
                            CostTotal = assistantMsg.Usage.Cost.Total,
                            CostInput = assistantMsg.Usage.Cost.Input,
                            CostOutput = assistantMsg.Usage.Cost.Output,
                            CostCacheRead = assistantMsg.Usage.Cost.CacheRead,
                            CostCacheWrite = assistantMsg.Usage.Cost.CacheWrite,
                        });
                    }
                    break;
            }
        }

        // Deliver result to the target channel+peer
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            await DeliverAsync(job, responseText.Trim(), ct);
        }

        return responseText;
    }

    private static IReadOnlyList<AgentTool> BuildJobTools(AgentDefinition agentDef)
    {
        var tools = new List<AgentTool>();

        // Add shared tools (SessionTool, MailTool, etc.) — but not CronTool
        foreach (var tool in agentDef.Tools)
        {
            tools.Add(tool);
        }

        // Add per-agent tools that make sense in isolation
        var sharedMemoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
        tools.Add(new MemoryTool(sharedMemoryPath, agentDef.MemoryPath));
        if (agentDef.TodoPath is { } todoPath)
            tools.Add(new TodoTool(todoPath));
        if (agentDef.CostLedger is { } costLedger)
            tools.Add(new CostTool(costLedger));

        return tools;
    }

    private async Task DeliverAsync(CronJob job, string text, CancellationToken ct)
    {
        var binding = _bindings.FirstOrDefault(b =>
            b.Name.Equals(job.Delivery.ChannelName, StringComparison.OrdinalIgnoreCase));

        if (binding is null)
        {
            _logger.LogWarning("Cron job '{Name}' delivery target channel '{Channel}' not found",
                job.Name, job.Delivery.ChannelName);
            return;
        }

        await binding.Transport.SendAsync(new TransportMessage
        {
            TransportId = binding.Transport.Id,
            PeerId = job.Delivery.PeerId,
            Text = text,
        }, ct);
    }

    private async Task UpdateJobStateAfterRunAsync(
        CronStore store, CronJob job, string status, string? error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nextRun = CronScheduler.ComputeNextRun(job.Schedule, now);

        await store.SaveJobStateAsync(job.Id, state =>
        {
            state.LastRunAt = now;
            state.LastStatus = status;
            state.LastError = error;
            state.NextRunAt = nextRun;

            if (status == "ok")
                state.ConsecutiveErrors = 0;
            else
                state.ConsecutiveErrors++;
        }, ct);

        // Disable exhausted one-shot jobs
        if (nextRun is null)
        {
            await store.DisableAsync(job.Id, ct);
            _logger.LogInformation("Disabled exhausted one-shot job '{Name}'", job.Name);
        }
    }
}
