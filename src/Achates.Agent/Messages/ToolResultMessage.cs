using Achates.Providers.Completions.Content;

namespace Achates.Agent.Messages;

public sealed record ToolResultMessage : AgentMessage
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlyList<CompletionUserContent> Content { get; init; }

    public bool IsError { get; init; }

    public object? Details { get; init; }
}
