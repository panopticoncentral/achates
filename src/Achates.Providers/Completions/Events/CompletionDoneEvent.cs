using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionDoneEvent : CompletionEvent
{
    public required CompletionStopReason Reason { get; init; }
    public required CompletionAssistantMessage CompletionMessage { get; init; }
}
