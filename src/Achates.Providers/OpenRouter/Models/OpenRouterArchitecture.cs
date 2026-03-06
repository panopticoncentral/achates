using System.Text.Json.Serialization;

namespace Achates.Providers.OpenRouter.Models;

internal sealed record OpenRouterArchitecture
{
    [JsonPropertyName("modality")]
    public string? Modality { get; init; }

    [JsonPropertyName("input_modalities")]
    public IReadOnlyList<string>? InputModalities { get; init; }

    [JsonPropertyName("output_modalities")]
    public IReadOnlyList<string>? OutputModalities { get; init; }

    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; init; }

    [JsonPropertyName("instruct_type")]
    public string? InstructType { get; init; }
}
