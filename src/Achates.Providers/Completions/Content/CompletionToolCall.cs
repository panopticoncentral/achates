namespace Achates.Providers.Completions.Content;

public sealed record CompletionToolCall : CompletionContent
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required Dictionary<string, object?> Arguments { get; init; }
}
