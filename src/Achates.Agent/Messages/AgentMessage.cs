namespace Achates.Agent.Messages;

/// <summary>
/// Base type for all messages in an agent conversation.
/// Subclass this to add application-specific message types.
/// </summary>
public abstract record AgentMessage
{
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
