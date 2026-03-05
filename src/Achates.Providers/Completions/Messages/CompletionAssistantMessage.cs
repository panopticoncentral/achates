using Achates.Providers.Completions.Content;

namespace Achates.Providers.Completions.Messages;

public sealed record CompletionAssistantMessage : CompletionMessage
{
    public required IReadOnlyList<CompletionContent> Content { get; init; }

    public required string Model { get; init; }

    public required CompletionUsage CompletionUsage { get; init; }

    public required CompletionStopReason CompletionStopReason { get; init; }

    public string? ErrorMessage { get; init; }
}
