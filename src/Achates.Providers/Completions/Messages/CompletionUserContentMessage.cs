using Achates.Providers.Completions.Content;

namespace Achates.Providers.Completions.Messages;

public sealed record CompletionUserContentMessage : CompletionUserMessage
{
    public required IReadOnlyList<CompletionUserContent> Content { get; init; }
}
