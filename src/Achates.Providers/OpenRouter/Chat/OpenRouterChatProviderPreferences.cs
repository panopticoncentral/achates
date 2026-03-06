using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

internal sealed record OpenRouterChatProviderPreferences
{
    [JsonPropertyName("allow_fallbacks")]
    public bool? AllowFallbacks { get; init; }

    [JsonPropertyName("require_parameters")]
    public bool? RequireParameters { get; init; }

    [JsonPropertyName("data_collection")]
    public string? DataCollection { get; init; }

    [JsonPropertyName("order")]
    public IReadOnlyList<string>? Order { get; init; }

    [JsonPropertyName("only")]
    public IReadOnlyList<string>? Only { get; init; }

    [JsonPropertyName("ignore")]
    public IReadOnlyList<string>? Ignore { get; init; }

    [JsonPropertyName("quantizations")]
    public IReadOnlyList<string>? Quantizations { get; init; }

    [JsonPropertyName("max_price")]
    public OpenRouterChatMaxPrice? MaxPrice { get; init; }
}

internal sealed record OpenRouterChatMaxPrice
{
    [JsonPropertyName("prompt")]
    public double? Prompt { get; init; }

    [JsonPropertyName("completion")]
    public double? Completion { get; init; }

    [JsonPropertyName("image")]
    public double? Image { get; init; }

    [JsonPropertyName("request")]
    public double? Request { get; init; }
}
