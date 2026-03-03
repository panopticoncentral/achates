using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatTool
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("function")]
    public required ChatFunction Function { get; init; }
}

public sealed record ChatFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }
}

public sealed record ChatToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("function")]
    public required ChatToolCallFunction Function { get; init; }
}

public sealed record ChatToolCallFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}
