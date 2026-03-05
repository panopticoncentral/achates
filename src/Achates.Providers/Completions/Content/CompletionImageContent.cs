namespace Achates.Providers.Completions.Content;

public sealed record CompletionImageContent : CompletionUserContent
{
    public required string Data { get; init; }

    public required string MimeType { get; init; }
}
