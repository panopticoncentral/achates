namespace Achates.Agent.Messages;

/// <summary>
/// A synthetic message that replaces a range of older messages with a summary.
/// Produced by session compaction when the conversation approaches the context limit.
/// </summary>
public sealed record SummaryMessage : AgentMessage
{
    public required string Summary { get; init; }
}
