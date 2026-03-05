using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions.Events;

public sealed record CompletionToolCallEndEvent : CompletionEvent
{
    public required int ContentIndex { get; init; }
    public required CompletionToolCall CompletionToolCall { get; init; }
    public required CompletionAssistantMessage Partial { get; init; }
}
