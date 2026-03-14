using System.Collections.Concurrent;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Transports;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Server.Cron;
using Achates.Server.Graph;
using Achates.Server.Tools;

namespace Achates.Server;

/// <summary>
/// The gateway wires channels (transport + agent bindings) to per-peer agent sessions.
/// Each unique channel+peer combination gets its own <see cref="AgentRuntime"/> instance
/// configured from the channel's agent definition.
/// </summary>
public sealed class Gateway : IAsyncDisposable
{
    private static readonly string SharedMemoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates", "memory.md");

    private readonly IReadOnlyList<ChannelBinding> _bindings;
    private readonly ISessionStore? _sessionStore;
    private readonly IReadOnlyDictionary<string, AgentInfo>? _agentRegistry;
    private readonly ConcurrentDictionary<string, AgentRuntime> _sessions = new();
    private readonly CancellationTokenSource _cts = new();

    public IReadOnlyList<ChannelBinding> Bindings => _bindings;
    public ICollection<string> ActiveSessionKeys => _sessions.Keys;
    public CronService? CronService { get; set; }

    /// <summary>
    /// Raised for every agent event.
    /// Useful for logging, rendering, or broadcasting.
    /// </summary>
    public event Func<AgentEvent, Task>? AgentEvent;

    public Gateway(IReadOnlyList<ChannelBinding> bindings, ISessionStore? sessionStore = null,
        IReadOnlyDictionary<string, AgentInfo>? agentRegistry = null)
    {
        _bindings = bindings;
        _sessionStore = sessionStore;
        _agentRegistry = agentRegistry;

        foreach (var binding in _bindings)
        {
            var b = binding; // capture for closure
            b.Transport.MessageReceived += msg => OnMessageReceivedAsync(b, msg);
        }
    }

    /// <summary>
    /// Start all transports and begin processing messages.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var binding in _bindings)
        {
            await binding.Transport.StartAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stop all transports and dispose resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        foreach (var binding in _bindings)
        {
            await binding.Transport.StopAsync();
        }

        foreach (var agent in _sessions.Values)
        {
            agent.Abort();
        }
    }

    public async Task RemoveSessionAsync(string sessionKey)
    {
        if (_sessions.TryRemove(sessionKey, out var existing))
            existing.Abort();

        if (_sessionStore is { } store)
            await store.DeleteAsync(sessionKey, _cts.Token);
    }

    private static async Task SendTypingLoopAsync(ITransport transport, string peerId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await transport.SendTypingAsync(peerId, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<AgentRuntime> GetOrCreateSessionAsync(
        string sessionKey, ChannelBinding binding)
    {
        if (_sessions.TryGetValue(sessionKey, out var existing))
            return existing;

        IReadOnlyList<AgentMessage>? savedMessages = null;
        if (_sessionStore is { } store)
        {
            savedMessages = await store.LoadAsync(sessionKey, _cts.Token);
        }

        var peerId = sessionKey[(binding.Name.Length + 1)..];
        var tools = BuildSessionTools(binding.Agent, binding.AgentName, binding.Name, peerId);

        var agent = new AgentRuntime(new AgentOptions
        {
            Model = binding.Agent.Model,
            SystemPrompt = binding.Agent.SystemPrompt,
            Tools = tools,
            CompletionOptions = binding.Agent.CompletionOptions,
            Messages = savedMessages,
        });

        return _sessions.GetOrAdd(sessionKey, agent);
    }

    private IReadOnlyList<AgentTool> BuildSessionTools(
        AgentDefinition agentDef, string agentName, string channelName, string peerId)
    {
        var tools = new List<AgentTool>(agentDef.Tools);
        tools.Add(new MemoryTool(SharedMemoryPath, agentDef.MemoryPath));
        if (agentDef.TodoPath is { } todoPath)
            tools.Add(new TodoTool(todoPath));
        if (agentDef.CostLedger is { } costLedger)
            tools.Add(new CostTool(costLedger));
        if (agentDef.CronStore is { } cronStore && CronService is { } cronService)
            tools.Add(new CronTool(cronStore, agentName, channelName, peerId, cronService));
        if (_agentRegistry is { Count: > 1 } registry
            && registry.TryGetValue(agentName, out var selfInfo)
            && selfInfo.ToolNames?.Contains("chat") == true)
            tools.Add(new ChatTool(agentName, registry, selfInfo.AllowChat));
        return tools;
    }

    private async Task OnMessageReceivedAsync(ChannelBinding binding, TransportMessage message)
    {
        var sessionKey = $"{binding.Name}:{message.PeerId}";

        // Handle /new command — reset the session
        if (message.Text.Trim().Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            if (_sessions.TryRemove(sessionKey, out var existing))
            {
                existing.Abort();
            }

            if (_sessionStore is { } store)
            {
                await store.DeleteAsync(sessionKey, _cts.Token);
            }

            await binding.Transport.SendAsync(new TransportMessage
            {
                TransportId = message.TransportId,
                PeerId = message.PeerId,
                Text = "Session reset. Starting fresh.",
            }, _cts.Token);
            return;
        }

        var agent = await GetOrCreateSessionAsync(sessionKey, binding);

        // If the agent is already running, queue as a follow-up
        if (agent.IsRunning)
        {
            agent.FollowUp(new UserMessage { Text = message.Text });
            return;
        }

        // Route device code sign-in messages to the user's chat
        GraphClient.SetDeviceCodeNotifier(msg =>
            binding.Transport.SendAsync(new TransportMessage
            {
                TransportId = message.TransportId,
                PeerId = message.PeerId,
                Text = msg,
            }, _cts.Token));

        var stream = agent.PromptAsync(message.Text);
        var responseText = "";

        // Start a typing indicator that repeats until processing finishes
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = SendTypingLoopAsync(binding.Transport, message.PeerId, typingCts.Token);

        await foreach (var evt in stream.WithCancellation(_cts.Token))
        {
            // Notify subscribers (for rendering, logging, etc.)
            if (AgentEvent is { } handler)
            {
                await handler(evt);
            }

            // Accumulate text for the outbound response
            switch (evt)
            {
                case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                    responseText += delta.Delta;
                    break;

                case MessageEndEvent { Message: AssistantMessage assistantMsg }:
                    // Record cost for every completion (including tool-use turns)
                    if (binding.Agent.CostLedger is { } costLedger)
                    {
                        _ = costLedger.AppendAsync(new CostEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Model = assistantMsg.Model,
                            Channel = binding.Name,
                            Peer = message.PeerId,
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

                    // Turn is done and not continuing with tools — send the response
                    if (assistantMsg.StopReason is not CompletionStopReason.ToolUse
                        && !string.IsNullOrWhiteSpace(responseText))
                    {
                        await binding.Transport.SendAsync(new TransportMessage
                        {
                            TransportId = message.TransportId,
                            PeerId = message.PeerId,
                            Text = responseText.Trim(),
                        }, _cts.Token);
                        responseText = "";
                    }
                    break;
            }
        }

        // Stop the typing indicator
        await typingCts.CancelAsync();

        // Catch any remaining text (e.g. from follow-up turns)
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            await binding.Transport.SendAsync(new TransportMessage
            {
                TransportId = message.TransportId,
                PeerId = message.PeerId,
                Text = responseText.Trim(),
            }, _cts.Token);
        }

        // Persist the updated conversation history
        if (_sessionStore is { } sessionStore)
        {
            await sessionStore.SaveAsync(sessionKey, agent.Messages, _cts.Token);
        }
    }
}
