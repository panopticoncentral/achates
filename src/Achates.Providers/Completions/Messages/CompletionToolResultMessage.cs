using Achates.Providers.Completions.Content;

namespace Achates.Providers.Completions.Messages;

public sealed record CompletionToolResultMessage : CompletionMessage
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlyList<CompletionUserContent> Content { get; init; }

    public object? Details { get; init; }

    public required bool IsError { get; init; }
}
