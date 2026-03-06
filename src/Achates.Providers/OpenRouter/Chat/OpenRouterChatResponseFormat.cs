using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatResponseFormat
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("json_schema")]
    public OpenRouterChatJsonSchema? JsonSchema { get; init; }
}

internal sealed record OpenRouterChatJsonSchema
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }
}
