using System.Collections.Concurrent;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Channels;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Server.Tools;

namespace Achates.Server;

/// <summary>
/// The gateway wires channels to per-peer agent sessions. Each unique channel+peer
/// combination gets its own agent with independent conversation history.
/// </summary>
public sealed class Gateway(GatewayOptions options) : IAsyncDisposable
{
    private readonly List<IChannel> _channels = [];
    private readonly ConcurrentDictionary<string, Agent.Agent> _sessions = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Raised for every agent event.
    /// Useful for logging, rendering, or broadcasting.
    /// </summary>
    public event Func<AgentEvent, Task>? AgentEvent;

    public IReadOnlyList<IChannel> Channels => _channels;

    /// <summary>
    /// Register a channel with the gateway. Must be called before <see cref="StartAsync"/>.
    /// </summary>
    public void AddChannel(IChannel channel)
    {
        channel.MessageReceived += msg => OnMessageReceivedAsync(channel, msg);
        _channels.Add(channel);
    }

    /// <summary>
    /// Start all registered channels and begin processing messages.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels)
        {
            await channel.StartAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stop all channels and dispose resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        foreach (var channel in _channels)
        {
            await channel.StopAsync();
        }

        foreach (var agent in _sessions.Values)
        {
            agent.Abort();
        }
    }

    private static async Task SendTypingLoopAsync(IChannel channel, string peerId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await channel.SendTypingAsync(peerId, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<Agent.Agent> GetOrCreateSessionAsync(string sessionKey, string channelId, string peerId)
    {
        if (_sessions.TryGetValue(sessionKey, out var existing))
            return existing;

        IReadOnlyList<AgentMessage>? savedMessages = null;
        if (options.SessionStore is { } store)
        {
            savedMessages = await store.LoadAsync(sessionKey, _cts.Token);
        }

        var tools = BuildSessionTools(channelId, peerId);

        var agent = new Agent.Agent(new AgentOptions
        {
            Model = options.Model,
            SystemPrompt = options.SystemPrompt,
            Tools = tools,
            CompletionOptions = options.CompletionOptions,
            Messages = savedMessages,
        });

        return _sessions.GetOrAdd(sessionKey, agent);
    }

    private IReadOnlyList<AgentTool>? BuildSessionTools(string channelId, string peerId)
    {
        if (options.Tools is not { Count: > 0 } && options.MemoryBasePath is null)
            return options.Tools;

        var tools = new List<AgentTool>(options.Tools ?? []);

        if (options.MemoryBasePath is { } memoryBase)
        {
            var memoryPath = Path.Combine(memoryBase, channelId, $"{peerId}.md");
            tools.Add(new MemoryTool(memoryPath));
        }

        return tools;
    }

    private async Task OnMessageReceivedAsync(IChannel channel, ChannelMessage message)
    {
        var sessionKey = $"{message.ChannelId}:{message.PeerId}";

        // Handle /new command — reset the session
        if (message.Text.Trim().Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            if (_sessions.TryRemove(sessionKey, out var existing))
            {
                existing.Abort();
            }

            if (options.SessionStore is { } store)
            {
                await store.DeleteAsync(sessionKey, _cts.Token);
            }

            await channel.SendAsync(new ChannelMessage
            {
                ChannelId = message.ChannelId,
                PeerId = message.PeerId,
                Text = "Session reset. Starting fresh.",
            }, _cts.Token);
            return;
        }

        var agent = await GetOrCreateSessionAsync(sessionKey, message.ChannelId, message.PeerId);

        // If the agent is already running, queue as a follow-up
        if (agent.IsRunning)
        {
            agent.FollowUp(new UserMessage { Text = message.Text });
            return;
        }

        var stream = agent.PromptAsync(message.Text);
        var responseText = "";

        // Start a typing indicator that repeats until processing finishes
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = SendTypingLoopAsync(channel, message.PeerId, typingCts.Token);

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

                case MessageEndEvent { Message: AssistantMessage { StopReason: not CompletionStopReason.ToolUse } }:
                    // Turn is done and not continuing with tools — send the response
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        await channel.SendAsync(new ChannelMessage
                        {
                            ChannelId = message.ChannelId,
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
            await channel.SendAsync(new ChannelMessage
            {
                ChannelId = message.ChannelId,
                PeerId = message.PeerId,
                Text = responseText.Trim(),
            }, _cts.Token);
        }

        // Persist the updated conversation history
        if (options.SessionStore is { } sessionStore)
        {
            await sessionStore.SaveAsync(sessionKey, agent.Messages, _cts.Token);
        }
    }
}
