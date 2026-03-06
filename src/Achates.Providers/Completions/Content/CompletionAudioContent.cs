namespace Achates.Providers.Completions.Content;

public sealed record CompletionAudioContent : CompletionContent
{
    public string? Id { get; init; }

    public required string Data { get; init; }

    public required string Format { get; init; }

    public string? Transcript { get; init; }
}
