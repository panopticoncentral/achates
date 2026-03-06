using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionAudioDeltaEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public string? DataDelta { get; init; }
    public string? TranscriptDelta { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
