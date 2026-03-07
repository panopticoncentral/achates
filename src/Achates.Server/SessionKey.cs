namespace Achates.Server;

/// <summary>
/// Deterministic session key that maps a channel + peer to a conversation.
/// </summary>
public readonly record struct SessionKey(string ChannelId, string PeerId)
{
    public override string ToString() => $"{ChannelId}:{PeerId}";

    public static SessionKey Parse(string value)
    {
        var sep = value.IndexOf(':');
        if (sep < 0)
        {
            throw new FormatException($"Invalid session key: {value}");
        }

        return new SessionKey(value[..sep], value[(sep + 1)..]);
    }
}
