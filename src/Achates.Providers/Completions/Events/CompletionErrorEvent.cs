using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionErrorEvent : CompletionEvent
{
    public required CompletionStopReason Reason { get; init; }
    public required CompletionAssistantMessage Error { get; init; }
}
