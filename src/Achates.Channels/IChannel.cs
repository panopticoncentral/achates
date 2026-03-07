namespace Achates.Channels;

/// <summary>
/// Abstraction for a messaging channel (console, Telegram, Discord, Slack, etc.).
/// Each channel has a lifecycle (start/stop) and can send/receive messages.
/// </summary>
public interface IChannel
{
    /// <summary>
    /// Unique identifier for this channel instance (e.g. "console", "telegram:12345").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Raised when a message arrives from this channel.
    /// </summary>
    event Func<ChannelMessage, Task> MessageReceived;

    /// <summary>
    /// Send a message out through this channel.
    /// </summary>
    Task SendAsync(ChannelMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start listening for messages. Called once by the gateway during startup.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop listening and clean up resources.
    /// </summary>
    Task StopAsync();
}
