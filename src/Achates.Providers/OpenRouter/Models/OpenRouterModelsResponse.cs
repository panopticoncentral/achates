using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

public sealed record OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public required IReadOnlyList<OpenRouterModel> Data { get; init; }
}

public sealed record OpenRouterModelsCountResponse
{
    [JsonPropertyName("data")]
    public required OpenRouterModelsCountData Data { get; init; }
}

public sealed record OpenRouterModelsCountData
{
    [JsonPropertyName("count")]
    public int Count { get; init; }
}
