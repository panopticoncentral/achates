using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OpenRouterChatMessage> Messages { get; init; }

    [JsonPropertyName("modalities")]
    public IReadOnlyList<string>? Modalities { get; init; }

    [JsonPropertyName("audio")]
    public OpenRouterChatAudioConfig? Audio { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("stream_options")]
    public OpenRouterChatStreamOptions? StreamOptions { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; init; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; init; }

    [JsonPropertyName("repetition_penalty")]
    public double? RepetitionPenalty { get; init; }

    [JsonPropertyName("min_p")]
    public double? MinP { get; init; }

    [JsonPropertyName("top_a")]
    public double? TopA { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; init; }

    [JsonPropertyName("logit_bias")]
    public IReadOnlyDictionary<string, double>? LogitBias { get; init; }

    [JsonPropertyName("seed")]
    public int? Seed { get; init; }

    [JsonPropertyName("stop")]
    public IReadOnlyList<string>? Stop { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenRouterChatTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; init; }

    [JsonPropertyName("response_format")]
    public OpenRouterChatResponseFormat? ResponseFormat { get; init; }

    [JsonPropertyName("logprobs")]
    public bool? Logprobs { get; init; }

    [JsonPropertyName("top_logprobs")]
    public int? TopLogprobs { get; init; }

    [JsonPropertyName("provider")]
    public OpenRouterChatProviderPreferences? Provider { get; init; }

    [JsonPropertyName("reasoning")]
    public OpenRouterChatReasoningConfig? Reasoning { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

internal sealed record OpenRouterChatAudioConfig
{
    [JsonPropertyName("voice")]
    public required string Voice { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }
}

internal sealed record OpenRouterChatStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool? IncludeUsage { get; init; }
}
