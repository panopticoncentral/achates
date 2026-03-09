namespace Achates.Transports;

/// <summary>
/// Abstraction for a messaging transport (WebSocket, Telegram, Discord, Slack, etc.).
/// Each transport has a lifecycle (start/stop) and can send/receive messages.
/// </summary>
public interface ITransport
{
    /// <summary>
    /// Unique identifier for this transport instance.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Raised when a message arrives from this transport.
    /// </summary>
    event Func<TransportMessage, Task> MessageReceived;

    /// <summary>
    /// Send a message out through this transport.
    /// </summary>
    Task SendAsync(TransportMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Show a typing indicator to the peer. Transports that don't support this can leave
    /// the default no-op. Callers should send this periodically (e.g. every 4–5 seconds)
    /// because some platforms expire the indicator.
    /// </summary>
    Task SendTypingAsync(string peerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Start listening for messages. Called once by the gateway during startup.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop listening and clean up resources.
    /// </summary>
    Task StopAsync();
}
