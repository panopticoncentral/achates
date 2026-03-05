namespace Achates.Providers.Completions.Content;

public sealed record CompletionThinkingContent : CompletionContent
{
    public required string Thinking { get; init; }
}
