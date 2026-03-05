using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionTextEndEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public required string Content { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
