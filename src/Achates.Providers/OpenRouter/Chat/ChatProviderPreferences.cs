using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Chat;

public sealed record ChatProviderPreferences
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
    public ChatMaxPrice? MaxPrice { get; init; }
}

public sealed record ChatMaxPrice
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
