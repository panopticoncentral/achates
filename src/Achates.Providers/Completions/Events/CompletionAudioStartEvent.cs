using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionAudioStartEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
