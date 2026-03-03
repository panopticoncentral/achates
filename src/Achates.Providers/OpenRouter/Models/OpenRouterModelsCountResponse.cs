using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

internal sealed record OpenRouterModelsCountResponse
{
    [JsonPropertyName("data")]
    public required OpenRouterModelsCountData Data { get; init; }
}
