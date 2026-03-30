namespace Achates.Providers.Completions.Content;

public sealed record CompletionImageContent : CompletionUserContent
{
    public required string Data { get; init; }

    public required string MimeType { get; init; }

    /// <summary>
    /// Optional relative URL to fetch the image from. When set, clients should prefer
    /// this over <see cref="Data"/> (which may be empty for lightweight timeline payloads).
    /// </summary>
    public string? Url { get; init; }
}
