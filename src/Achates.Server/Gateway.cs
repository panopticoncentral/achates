using System.Collections.Concurrent;
using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Channels;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;

namespace Achates.Server;

/// <summary>
/// The gateway wires channels to agents. It manages channel lifecycle,
/// routes inbound messages to per-session agents, and delivers responses back.
/// </summary>
public sealed class Gateway : IAsyncDisposable
{
    private readonly List<IChannel> _channels = [];
    private readonly ConcurrentDictionary<SessionKey, Agent.Agent> _sessions = new();
    private readonly GatewayOptions _options;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Raised for every agent event across all sessions.
    /// Useful for logging, rendering, or broadcasting.
    /// </summary>
    public event Func<SessionKey, AgentEvent, Task>? AgentEvent;

    public Gateway(GatewayOptions options)
    {
        _options = options;
    }

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

        foreach (var (_, agent) in _sessions)
        {
            agent.Abort();
        }
    }

    /// <summary>
    /// Get or create the agent session for a given channel + peer.
    /// </summary>
    public Agent.Agent GetOrCreateSession(SessionKey key)
    {
        return _sessions.GetOrAdd(key, _ => CreateAgent());
    }

    private Agent.Agent CreateAgent()
    {
        return new Agent.Agent(new AgentOptions
        {
            Model = _options.Model,
            SystemPrompt = _options.SystemPrompt,
            Tools = _options.Tools,
            CompletionOptions = _options.CompletionOptions,
        });
    }

    private async Task OnMessageReceivedAsync(IChannel channel, ChannelMessage message)
    {
        var key = new SessionKey(message.ChannelId, message.PeerId);
        var agent = GetOrCreateSession(key);

        // If the agent is already running, queue as a follow-up
        if (agent.IsRunning)
        {
            agent.FollowUp(new UserMessage { Text = message.Text });
            return;
        }

        var stream = agent.PromptAsync(message.Text);
        var responseText = "";

        await foreach (var evt in stream.WithCancellation(_cts.Token))
        {
            // Notify subscribers (for rendering, logging, etc.)
            if (AgentEvent is { } handler)
            {
                await handler(key, evt);
            }

            // Accumulate text for the outbound response
            switch (evt)
            {
                case MessageStreamEvent { Inner: CompletionTextDeltaEvent delta }:
                    responseText += delta.Delta;
                    break;

                case MessageEndEvent { Message: AssistantMessage assistant }
                    when assistant.StopReason is not CompletionStopReason.ToolUse:
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
    }
}
