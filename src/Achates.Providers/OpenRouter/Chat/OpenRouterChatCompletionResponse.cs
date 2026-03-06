using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatCompletionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; init; }

    [JsonPropertyName("choices")]
    public required IReadOnlyList<OpenRouterChatChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public OpenRouterChatUsage? Usage { get; init; }
}

internal sealed record OpenRouterChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("message")]
    public required OpenRouterChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("logprobs")]
    public JsonElement? Logprobs { get; init; }
}
