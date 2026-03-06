using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenRouterChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; init; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<OpenRouterChatContentPart>? Images { get; init; }

    [JsonPropertyName("audio")]
    public OpenRouterChatAudioResponse? Audio { get; init; }
}

internal sealed record OpenRouterChatAudioResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("transcript")]
    public string? Transcript { get; init; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAt { get; init; }
}

internal sealed record OpenRouterChatContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    public OpenRouterChatImageUrl? ImageUrl { get; init; }

    [JsonPropertyName("file")]
    public OpenRouterChatFileData? File { get; init; }

    [JsonPropertyName("input_audio")]
    public OpenRouterChatInputAudio? InputAudio { get; init; }
}

internal sealed record OpenRouterChatInputAudio
{
    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }
}

internal sealed record OpenRouterChatImageUrl
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

internal sealed record OpenRouterChatFileData
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("file_data")]
    public required string FileData { get; init; }
}
