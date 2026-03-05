using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionStartEvent : CompletionEvent
{
    public required CompletionAssistantMessage Partial { get; init; }
}
