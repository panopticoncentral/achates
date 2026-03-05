namespace Achates.Providers.Completions.Content;

public sealed record CompletionFileContent : CompletionUserContent
{
    public required string Data { get; init; }

    public required string MimeType { get; init; }

    public string? FileName { get; init; }
}
