namespace Achates.Providers.Completions.Content;

public sealed record CompletionTextContent : CompletionUserContent
{
    public required string Text { get; init; }
}
