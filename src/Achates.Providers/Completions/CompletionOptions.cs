namespace Achates.Providers.Completions;

public sealed record CompletionOptions
{
    // Sampling
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public double? MinP { get; init; }
    public double? TopA { get; init; }
    public IReadOnlyDictionary<int, double>? LogitBias { get; init; }

    // Penalties
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
    public double? RepetitionPenalty { get; init; }

    // Output control
    public int? MaxTokens { get; init; }
    public int? Seed { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }

    // Logprobs
    public bool? Logprobs { get; init; }
    public int? TopLogprobs { get; init; }

    // Tool use
    public CompletionToolChoice? ToolChoice { get; init; }
    public bool? ParallelToolCalls { get; init; }

    // Response format
    public CompletionResponseFormat? ResponseFormat { get; init; }

    // Reasoning
    public string? ReasoningEffort { get; init; }
}
