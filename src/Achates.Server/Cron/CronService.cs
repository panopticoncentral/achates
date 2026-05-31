using System.Collections.Concurrent;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Server.Mobile;
using Achates.Server.Tools;

namespace Achates.Server.Cron;

/// <summary>
/// Background service that manages a timer loop, finds due cron jobs, and executes them.
/// Created manually by GatewayService after agents are resolved.
/// </summary>
public sealed class CronService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (CronStore Store, AgentDefinition Agent)> _agents;
    private readonly MobileTransport _transport;
    private readonly MobileSessionStore _sessionStore;
    private readonly CronSessionReaper? _reaper;
    private readonly ILogger<CronService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _poke = new(0);
    private Task? _loopTask;

    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(2);
    private const int MaxJobTurns = 20;
    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(15);

    // Upper bound on how far back a single dreamtime run reviews. Caps the load when
    // LastRunAt is stuck after a failure streak; normal daily runs and short backlogs
    // are well under this and unaffected.
    private static readonly TimeSpan MaxDreamtimeReviewWindow = TimeSpan.FromDays(14);

    private const string DreamtimeInstructions = """

        --- Dreamtime Mode ---

        You are performing your nightly memory review.

        You have access to your current memory and the sessions that have occurred since
        your last review. Your job is to:

        1. Use the sessions tool to list recent sessions.
        2. Scan the list and decide which sessions contain information worth remembering.
        3. Read those sessions in full.
        4. Read your current memory.
        5. Update your memory to incorporate new learnings. Prefer INCREMENTAL edits:
           use the memory tool's `append` action to add new facts and its `edit` action
           to correct or remove specific outdated lines. Reserve a full `save` rewrite
           for when the memory has genuinely become disorganized and needs restructuring
           — a full save regenerates the entire file in one shot and is slow and failure-
           prone once the memory is large.

        Focus on:
        - User preferences, habits, and facts you've learned
        - Recurring requests or patterns
        - Corrections the user made
        - Things the user explicitly asked you to remember
        - Outdated information in memory that sessions contradict

        Do NOT memorize transient details (specific appointment times, one-off questions).
        Focus on durable knowledge that will help you serve the user better.

        When you're done, briefly summarize what you changed and why.
        """;

    public CronService(
        Dictionary<string, (CronStore Store, AgentDefinition Agent)> agents,
        MobileTransport transport,
        MobileSessionStore sessionStore,
        ILogger<CronService> logger,
        CronSessionReaper? reaper = null)
    {
        _agents = new ConcurrentDictionary<string, (CronStore, AgentDefinition)>(agents);
        _transport = transport;
        _sessionStore = sessionStore;
        _logger = logger;
        _reaper = reaper;
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
    /// Re-key an agent entry after rename. Uses the CronStore from the new definition
    /// (which was re-resolved with the new directory path).
    /// </summary>
    public void RenameAgent(string oldName, string newName, AgentDefinition newDefinition)
    {
        _agents.TryRemove(oldName, out _);
        if (newDefinition.CronStore is { } store)
            _agents[newName] = (store, newDefinition);
    }

    /// <summary>
    /// Register an agent created or reloaded after startup so its cron store is
    /// polled by the loop. No-op if the agent has no CronStore. Pokes the loop
    /// so a freshly added (possibly overdue) job is picked up promptly.
    /// </summary>
    public void AddAgent(string name, AgentDefinition definition)
    {
        if (definition.CronStore is not { } store) return;
        _agents[name] = (store, definition);
        Poke();
    }

    /// <summary>
    /// Drop an agent from the cron service. The agent's cron.json is deleted by the caller
    /// as part of wiping the agent directory.
    /// </summary>
    public void RemoveAgent(string name)
    {
        if (_agents.TryRemove(name, out _))
            Poke();
    }

    /// <summary>
    /// Run a specific job immediately. Returns the result text.
    /// When <paramref name="advanceSchedule"/> is true, the manual run also advances
    /// <c>LastRunAt</c>/<c>NextRunAt</c> as if the job had fired on schedule, effectively
    /// skipping the next scheduled occurrence.
    /// </summary>
    public async Task<string?> RunJobAsync(
        string agentName, string jobId,
        bool advanceSchedule = false,
        CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentName, out var entry))
            return null;

        var jobs = await entry.Store.LoadAsync(ct);
        var job = jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) return null;

        var (result, skipped, runError) = await ExecuteJobAsync(agentName, entry.Agent, entry.Store, job, ct);

        if (advanceSchedule)
        {
            if (skipped)
                await UpdateJobStateAfterRunAsync(entry.Store, job, "skipped", null, ct, advanceLastRunAt: false);
            else if (runError is not null)
                await UpdateJobStateAfterRunAsync(entry.Store, job, "error", runError, ct, advanceLastRunAt: false);
            else
                await UpdateJobStateAfterRunAsync(entry.Store, job, "ok", null, ct);

            Poke();
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _cts.CancelAsync(); }
        catch (ObjectDisposedException) { }

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { }

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
                        var (_, skipped, runError) = await ExecuteJobAsync(agentName, agent, store, job, ct);
                        if (skipped)
                        {
                            await UpdateJobStateAfterRunAsync(store, job, "skipped", null, ct, advanceLastRunAt: false);
                            _logger.LogInformation("Cron job '{Name}' ({Id}) skipped", job.Name, job.Id);
                        }
                        else if (runError is not null)
                        {
                            // The agent ran but a completion ended in error (e.g. network drop mid-stream).
                            // Don't advance LastRunAt so dreamtime can re-review the same sessions next run.
                            await UpdateJobStateAfterRunAsync(store, job, "error", runError, ct, advanceLastRunAt: false);
                            _logger.LogWarning("Cron job '{Name}' ({Id}) ended with error: {Error}", job.Name, job.Id, runError);
                        }
                        else
                        {
                            await UpdateJobStateAfterRunAsync(store, job, "ok", null, ct);
                            _logger.LogInformation("Cron job '{Name}' ({Id}) completed", job.Name, job.Id);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await UpdateJobStateAfterRunAsync(store, job, "error", ex.Message, ct, advanceLastRunAt: false);
                        _logger.LogError(ex, "Cron job '{Name}' ({Id}) failed", job.Name, job.Id);
                    }
                }

                await SweepOldSessionsAsync(ct);
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

    private async Task<(string? Result, bool Skipped, string? Error)> ExecuteJobAsync(
        string agentName, AgentDefinition agentDef, CronStore store, CronJob job, CancellationToken ct)
    {
        _logger.LogInformation("Executing cron job '{Name}' ({Id}) for agent '{Agent}'",
            job.Name, job.Id, agentName);

        // Build tool list and system prompt — dreamtime jobs get special treatment.
        // The system prompt is date-free; temporal context is injected per-turn at
        // the tail of the outgoing payload via TemporalContext.CreateTransform().
        var systemPrompt = agentDef.SystemPrompt;
        var tools = BuildJobTools(agentName, agentDef);

        if (job.Kind == CronJobKind.Dreamtime)
        {
            // Skip if no sessions have been updated since the last review
            var (latest, _) = await _sessionStore.ListAsync(agentName, limit: 1, ct: ct);
            var since = job.State.LastRunAt;
            var hasNew = latest.Any(s => since is null || s.Updated > since.Value);
            if (!hasNew)
            {
                _logger.LogInformation(
                    "Skipping dreamtime for agent '{Agent}' — no new sessions since {Since}",
                    agentName, since?.ToString("o") ?? "ever");
                return ("Skipped: no sessions to review since last dreamtime.", true, null);
            }

            systemPrompt = agentDef.SystemPrompt + DreamtimeInstructions;
            tools = BuildDreamtimeTools(agentName, agentDef, job);
        }

        var agent = new AgentRuntime(new AgentOptions
        {
            Model = agentDef.Model,
            SystemPrompt = systemPrompt,
            Tools = tools,
            CompletionOptions = agentDef.CompletionOptions,
            TransformContext = TemporalContext.CreateTransform(),
        });

        var stream = agent.PromptAsync(new UserMessage
        {
            Text = $"{CronSessionMarker.FormatHeader(job.Name)}\n\n{job.Message}",
            Hidden = true,
        });

        var (responseText, streamError) = await ConsumeJobStreamAsync(
            stream, agentDef.CostLedger, job, MaxJobDuration, _logger, ct);

        // Save as a session so results are visible in the app
        if (agent.Messages.Count > 0)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var session = new MobileSession
            {
                Id = sessionId,
                Title = job.Name,
                JobId = job.Id,
                Messages = [.. agent.Messages],
            };
            await _sessionStore.SaveAsync(agentName, session, ct);

            // Also notify the active connection in real time
            await DeliverAsync(job, agentName, session, ct);
        }

        return (responseText, false, streamError);
    }

    /// <summary>
    /// Consumes a job's agent event stream, accumulating response text and recording cost.
    /// Bounded by both <see cref="MaxJobTurns"/> and a wall-clock <paramref name="timeout"/>
    /// so a stalled run can never freeze the cron loop. Service shutdown
    /// (<paramref name="serviceCt"/>) propagates as <see cref="OperationCanceledException"/>;
    /// a timeout is reported via the returned error string instead of throwing.
    /// </summary>
    internal static async Task<(string Response, string? Error)> ConsumeJobStreamAsync(
        IAsyncEnumerable<AgentEvent> stream,
        CostLedger? costLedger,
        CronJob job,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken serviceCt)
    {
        var responseText = "";
        var turnCount = 0;
        string? streamError = null;
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        turnCts.CancelAfter(timeout);

        try
        {
            await foreach (var evt in stream.WithCancellation(turnCts.Token))
            {
                switch (evt)
                {
                    case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                        responseText += delta.Delta;
                        break;

                    case MessageEndEvent { Message: Agent.Messages.AssistantMessage assistantMsg }:
                        turnCount++;

                        if (assistantMsg.Error is { Length: > 0 } err)
                            streamError = err;
                        else if (assistantMsg.StopReason == CompletionStopReason.Error)
                            streamError = $"Completion ended with stop reason '{assistantMsg.StopReason}'.";

                        // Record cost
                        if (costLedger is not null)
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

                        if (turnCount >= MaxJobTurns)
                        {
                            logger.LogWarning("Cron job '{Name}' ({Id}) reached max turn limit ({Max})",
                                job.Name, job.Id, MaxJobTurns);
                            await turnCts.CancelAsync();
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (serviceCt.IsCancellationRequested)
        {
            // Service shutdown — let it propagate so the loop exits cleanly.
            throw;
        }
        catch (OperationCanceledException) when (turnCount >= MaxJobTurns)
        {
            responseText += "\n\n[Stopped: reached maximum turn limit]";
        }
        catch (OperationCanceledException)
        {
            // Wall-clock timeout fired (not shutdown, not turn limit). Treat as an
            // error so the schedule advances to the next occurrence and the loop
            // moves on instead of staying frozen on this job.
            var minutes = (int)Math.Round(timeout.TotalMinutes);
            logger.LogWarning("Cron job '{Name}' ({Id}) exceeded {Minutes} minute time limit",
                job.Name, job.Id, minutes);
            responseText += $"\n\n[Stopped: job exceeded {minutes} minute time limit]";
            streamError = $"Timed out after {minutes} minutes.";
        }

        return (responseText, streamError);
    }

    private IReadOnlyList<AgentTool> BuildJobTools(string agentName, AgentDefinition agentDef)
    {
        var tools = new List<AgentTool>();

        // Add shared tools (SessionTool, MailTool, etc.) — but not CronTool
        foreach (var tool in agentDef.Tools)
        {
            tools.Add(tool);
        }

        var sharedMemoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
        tools.AddRange(UniversalTools.Build(agentName, agentDef, sharedMemoryPath, BuildCostLedgerRegistry()));

        return tools;
    }

    private IReadOnlyList<AgentTool> BuildDreamtimeTools(string agentName, AgentDefinition agentDef, CronJob job)
    {
        var tools = new List<AgentTool>();

        // Session browser — review since the last run, but never look back further than
        // MaxDreamtimeReviewWindow so a stuck LastRunAt (after repeated failures) can't
        // make one run grind through weeks of sessions. A null (never-run) or recent
        // LastRunAt is left untouched.
        var since = job.State.LastRunAt;
        if (since is { } last && last < DateTimeOffset.UtcNow - MaxDreamtimeReviewWindow)
        {
            since = DateTimeOffset.UtcNow - MaxDreamtimeReviewWindow;
        }
        tools.Add(new SessionsTool(_sessionStore, agentName, currentSessionId: null, since));

        var sharedMemoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");
        tools.AddRange(UniversalTools.Build(agentName, agentDef, sharedMemoryPath, BuildCostLedgerRegistry()));

        return tools;
    }

    private IReadOnlyDictionary<string, CostLedger> BuildCostLedgerRegistry() =>
        _agents
            .Where(kv => kv.Value.Agent.CostLedger is not null)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Agent.CostLedger!,
                StringComparer.OrdinalIgnoreCase);

    private async Task DeliverAsync(CronJob job, string agentName, MobileSession session, CancellationToken ct)
    {
        // Invalidate cache so next agents.list picks up the new session
        _transport.InvalidateAgentCache(agentName);

        // Push the new session into every client's session list
        await _transport.BroadcastSessionUpdatedAsync(agentName, session, ct);

        // Broadcast to all connected clients; result is also persisted as a session
        await _transport.BroadcastEventAsync("cron.result", new
        {
            agent = agentName,
            session_id = session.Id,
            job_id = job.Id,
            job_name = job.Name,
        }, ct);

        // Signal done so clients refresh agent list (summary + unread count)
        await _transport.BroadcastEventAsync("done", new
        {
            agent = agentName,
            session_id = session.Id,
        }, ct);
    }

    private async Task SweepOldSessionsAsync(CancellationToken ct)
    {
        if (_reaper is null) return;

        var input = new List<(string, IReadOnlyList<CronJob>)>(_agents.Count);
        foreach (var (agentName, (store, _)) in _agents)
        {
            var jobs = await store.LoadAsync(ct);
            input.Add((agentName, jobs));
        }

        await _reaper.SweepAsync(input, ct);
    }

    private async Task UpdateJobStateAfterRunAsync(
        CronStore store, CronJob job, string status, string? error, CancellationToken ct,
        bool advanceLastRunAt = true)
    {
        var now = DateTimeOffset.UtcNow;
        var nextRun = CronScheduler.ComputeNextRun(job.Schedule, now);

        await store.SaveJobStateAsync(job.Id, state =>
        {
            if (advanceLastRunAt)
                state.LastRunAt = now;
            state.LastStatus = status;
            state.LastError = error;
            state.NextRunAt = nextRun;

            if (status == "ok")
                state.ConsecutiveErrors = 0;
            else if (status != "skipped")
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
