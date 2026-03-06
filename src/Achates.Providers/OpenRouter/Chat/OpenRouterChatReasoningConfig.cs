using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatReasoningConfig
{
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("exclude")]
    public bool? Exclude { get; init; }
}
