namespace Achates.Channels;

/// <summary>
/// A message received from or sent to a channel.
/// </summary>
public sealed record ChannelMessage
{
    /// <summary>
    /// The channel that originated or will receive this message.
    /// </summary>
    public required string ChannelId { get; init; }

    /// <summary>
    /// Identity of the peer (user, group, thread) within the channel.
    /// For console this is fixed; for Telegram it might be a chat ID, etc.
    /// </summary>
    public required string PeerId { get; init; }

    /// <summary>
    /// The text content of the message.
    /// </summary>
    public required string Text { get; init; }

    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
