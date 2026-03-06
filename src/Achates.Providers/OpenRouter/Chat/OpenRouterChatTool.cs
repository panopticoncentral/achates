using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatTool
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("function")]
    public required OpenRouterChatFunction Function { get; init; }
}

internal sealed record OpenRouterChatFunction
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

internal sealed record OpenRouterChatToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("function")]
    public required OpenRouterChatToolCallFunction Function { get; init; }
}

internal sealed record OpenRouterChatToolCallFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}
