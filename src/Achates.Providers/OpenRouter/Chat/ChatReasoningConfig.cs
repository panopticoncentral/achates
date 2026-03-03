using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatReasoningConfig
{
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("exclude")]
    public bool? Exclude { get; init; }
}
