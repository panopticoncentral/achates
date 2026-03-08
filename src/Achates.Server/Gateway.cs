using Achates.Agent;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Channels;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;

namespace Achates.Server;

/// <summary>
/// The gateway wires channels to a single shared agent. It manages channel lifecycle,
/// routes inbound messages to the agent, and delivers responses back.
/// </summary>
public sealed class Gateway(GatewayOptions options) : IAsyncDisposable
{
    private readonly List<IChannel> _channels = [];
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The shared agent that handles all conversations.
    /// </summary>
    public Agent.Agent Agent { get; } = new(new AgentOptions
    {
        Model = options.Model,
        SystemPrompt = options.SystemPrompt,
        Tools = options.Tools,
        CompletionOptions = options.CompletionOptions,
    });

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

        Agent.Abort();
    }

    private async Task OnMessageReceivedAsync(IChannel channel, ChannelMessage message)
    {
        // If the agent is already running, queue as a follow-up
        if (Agent.IsRunning)
        {
            Agent.FollowUp(new UserMessage { Text = message.Text });
            return;
        }

        var stream = Agent.PromptAsync(message.Text);
        var responseText = "";

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
