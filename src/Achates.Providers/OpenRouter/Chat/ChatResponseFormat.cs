using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatResponseFormat
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("json_schema")]
    public ChatJsonSchema? JsonSchema { get; init; }
}

public sealed record ChatJsonSchema
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
