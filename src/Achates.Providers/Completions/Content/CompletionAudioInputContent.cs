namespace Achates.Providers.Completions.Content;

public sealed record CompletionAudioInputContent : CompletionUserContent
{
    public required string Data { get; init; }

    public required string Format { get; init; }
}
