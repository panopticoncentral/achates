namespace Achates.Providers.Models;

/// <summary>
/// Flags representing the parameters supported by an LLM.
/// </summary>
[Flags]
public enum ModelParameters
{
    /// <summary>
    /// Controls randomness of output. Higher values produce more varied responses.
    /// </summary>
    Temperature = 0x00000000,

    /// <summary>
    /// Nucleus sampling: limits token selection to a cumulative probability threshold.
    /// </summary>
    TopP = 0x00000001,

    /// <summary>
    /// Limits token selection to the top K most probable tokens.
    /// </summary>
    TopK = 0x00000002,

    /// <summary>
    /// Minimum probability threshold for token selection.
    /// </summary>
    MinP = 0x00000004,

    /// <summary>
    /// Top-A sampling: filters tokens based on the probability of the most likely token.
    /// </summary>
    TopA = 0x00000008,

    /// <summary>
    /// Penalizes tokens based on their frequency in the generated text so far.
    /// </summary>
    FrequencyPenalty = 0x00000010,

    /// <summary>
    /// Penalizes tokens that have already appeared in the generated text.
    /// </summary>
    PresencePenalty = 0x00000020,

    /// <summary>
    /// Penalizes repeated sequences of tokens to reduce repetitive output.
    /// </summary>
    RepetitionPenalty = 0x00000040,

    /// <summary>
    /// Maximum number of tokens to generate in the response.
    /// </summary>
    MaxTokens = 0x00000080,

    /// <summary>
    /// Allows biasing the likelihood of specific tokens during generation.
    /// </summary>
    LogitBias = 0x00000100,

    /// <summary>
    /// Returns the log probabilities of output tokens.
    /// </summary>
    LogProbs = 0x00000200,

    /// <summary>
    /// Returns the top N most probable tokens and their log probabilities.
    /// </summary>
    TopLogProbs = 0x00000400,

    /// <summary>
    /// Sets a seed for deterministic generation across repeated requests.
    /// </summary>
    Seed = 0x00000800,

    /// <summary>
    /// Specifies the format of the response (e.g., JSON mode).
    /// </summary>
    ResponseFormat = 0x00001000,

    /// <summary>
    /// Enables structured output with a defined schema.
    /// </summary>
    StructuredOutputs = 0x00002000,

    /// <summary>
    /// Specifies stop sequences that halt generation when encountered.
    /// </summary>
    Stop = 0x00004000,

    /// <summary>
    /// Defines the tools (functions) available for the model to call.
    /// </summary>
    Tools = 0x00008000,

    /// <summary>
    /// Controls how the model selects which tool to call.
    /// </summary>
    ToolChoice = 0x00010000,

    /// <summary>
    /// Allows the model to invoke multiple tools in parallel.
    /// </summary>
    ParallelToolCalls = 0x00020000,

    /// <summary>
    /// Includes the model's internal reasoning in the response.
    /// </summary>
    IncludeReasoning = 0x00040000,

    /// <summary>
    /// Enables extended thinking or chain-of-thought reasoning.
    /// </summary>
    Reasoning = 0x00080000,

    /// <summary>
    /// Controls the depth of reasoning (e.g., low, medium, high).
    /// </summary>
    ReasoningEffort = 0x00100000,

    /// <summary>
    /// Enables web search capabilities for grounding responses in live data.
    /// </summary>
    WebSearchOptions = 0x00200000,

    /// <summary>
    /// Controls the verbosity level of the model's response.
    /// </summary>
    Verbosity = 0x00400000
}
