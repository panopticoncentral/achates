using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatCompletionResponse
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
    public required IReadOnlyList<ChatChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public ChatUsage? Usage { get; init; }
}

public sealed record ChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("logprobs")]
    public JsonElement? Logprobs { get; init; }
}
