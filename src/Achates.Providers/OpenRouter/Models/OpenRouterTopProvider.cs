using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

public sealed record OpenRouterTopProvider
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; init; }

    [JsonPropertyName("is_moderated")]
    public bool IsModerated { get; init; }
}
