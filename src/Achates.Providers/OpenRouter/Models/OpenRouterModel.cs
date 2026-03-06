using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

internal sealed record OpenRouterModel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("canonical_slug")]
    public required string CanonicalSlug { get; init; }

    [JsonPropertyName("hugging_face_id")]
    public string? HuggingFaceId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; init; }

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; init; }

    [JsonPropertyName("top_provider")]
    public OpenRouterTopProvider? TopProvider { get; init; }

    [JsonPropertyName("per_request_limits")]
    public JsonElement? PerRequestLimits { get; init; }

    [JsonPropertyName("supported_parameters")]
    public IReadOnlyList<string>? SupportedParameters { get; init; }

    [JsonPropertyName("default_parameters")]
    public JsonElement? DefaultParameters { get; init; }

    [JsonPropertyName("expiration_date")]
    public string? ExpirationDate { get; init; }
}
