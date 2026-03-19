namespace Achates.Server;

/// <summary>
/// A message received from or sent to a transport.
/// </summary>
public sealed record TransportMessage
{
    /// <summary>
    /// The transport that originated or will receive this message.
    /// </summary>
    public required string TransportId { get; init; }

    /// <summary>
    /// Identity of the peer (user, group, thread) within the transport.
    /// For console this is fixed; for WebSocket it might be an auto-assigned ID, etc.
    /// </summary>
    public required string PeerId { get; init; }

    /// <summary>
    /// The text content of the message.
    /// </summary>
    public required string Text { get; init; }

    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
