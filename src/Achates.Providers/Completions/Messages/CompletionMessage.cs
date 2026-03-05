namespace Achates.Providers.Completions.Messages;

/// <summary>
/// Base type for all messages in a conversation.
/// </summary>
public abstract record CompletionMessage
{
    public long Timestamp { get; init; }
}
