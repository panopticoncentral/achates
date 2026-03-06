using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatCompletionChunk
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
    public required IReadOnlyList<OpenRouterChatChunkChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public OpenRouterChatUsage? Usage { get; init; }
}

internal sealed record OpenRouterChatChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public required OpenRouterChatDelta Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("logprobs")]
    public JsonElement? Logprobs { get; init; }
}

internal sealed record OpenRouterChatDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenRouterChatDeltaToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; init; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<OpenRouterChatContentPart>? Images { get; init; }

    [JsonPropertyName("audio")]
    public OpenRouterChatAudioDelta? Audio { get; init; }
}

internal sealed record OpenRouterChatAudioDelta
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("transcript")]
    public string? Transcript { get; init; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAt { get; init; }
}

internal sealed record OpenRouterChatDeltaToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public OpenRouterChatDeltaToolCallFunction? Function { get; init; }
}

internal sealed record OpenRouterChatDeltaToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}
