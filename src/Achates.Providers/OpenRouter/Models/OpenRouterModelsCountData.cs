using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

internal sealed record OpenRouterModelsCountData
{
    [JsonPropertyName("count")]
    public int Count { get; init; }
}
