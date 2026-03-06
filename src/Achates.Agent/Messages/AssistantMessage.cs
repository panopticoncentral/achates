using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;

namespace Achates.Agent.Messages;

public sealed record AssistantMessage : AgentMessage
{
    public required IReadOnlyList<CompletionContent> Content { get; init; }

    public required string Model { get; init; }

    public required CompletionUsage Usage { get; init; }

    public required CompletionStopReason StopReason { get; init; }

    public string? Error { get; init; }
}
