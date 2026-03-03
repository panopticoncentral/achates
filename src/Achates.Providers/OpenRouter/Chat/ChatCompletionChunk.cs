using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatCompletionChunk
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
    public required IReadOnlyList<ChatChunkChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public ChatUsage? Usage { get; init; }
}

public sealed record ChatChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public required ChatDelta Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }

    [JsonPropertyName("logprobs")]
    public JsonElement? Logprobs { get; init; }
}

public sealed record ChatDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ChatDeltaToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; init; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }
}

public sealed record ChatDeltaToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public ChatDeltaToolCallFunction? Function { get; init; }
}

public sealed record ChatDeltaToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}
