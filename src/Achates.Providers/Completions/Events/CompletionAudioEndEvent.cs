using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionAudioEndEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public required CompletionAudioContent Content { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
