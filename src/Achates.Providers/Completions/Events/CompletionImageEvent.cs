using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionImageEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public required CompletionImageContent Image { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
