using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

public sealed record OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("completion")]
    public string? Completion { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("audio")]
    public string? Audio { get; init; }

    [JsonPropertyName("request")]
    public string? Request { get; init; }

    [JsonPropertyName("web_search")]
    public string? WebSearch { get; init; }

    [JsonPropertyName("internal_reasoning")]
    public string? InternalReasoning { get; init; }

    [JsonPropertyName("input_cache_read")]
    public string? InputCacheRead { get; init; }

    [JsonPropertyName("input_cache_write")]
    public string? InputCacheWrite { get; init; }
}
