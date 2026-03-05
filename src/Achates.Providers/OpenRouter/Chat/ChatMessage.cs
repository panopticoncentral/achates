using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; init; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }
}

public sealed record ChatContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    public ChatImageUrl? ImageUrl { get; init; }

    [JsonPropertyName("file")]
    public ChatFileData? File { get; init; }
}

public sealed record ChatImageUrl
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record ChatFileData
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("file_data")]
    public required string FileData { get; init; }
}
