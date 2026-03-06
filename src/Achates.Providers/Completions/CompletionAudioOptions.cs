namespace Achates.Providers.Completions;

public sealed record CompletionAudioOptions
{
    public required string Voice { get; init; }

    public required string Format { get; init; }
}
