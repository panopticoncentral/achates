namespace Achates.Providers.Models;

/// <summary>
/// Represents the per-unit pricing structure for an LLM.
/// All token costs are expressed as cost per token unless otherwise noted.
/// </summary>
public sealed record ModelCost
{
    /// <summary>
    /// Cost per input (prompt) token.
    /// </summary>
    public required decimal Prompt { get; init; }

    /// <summary>
    /// Cost per output (completion) token.
    /// </summary>
    public required decimal Completion { get; init; }

    /// <summary>
    /// Fixed cost per API request, independent of token count.
    /// </summary>
    public decimal? Request { get; init; }

    /// <summary>
    /// Cost per input image.
    /// </summary>
    public decimal? Image { get; init; }

    /// <summary>
    /// Cost per token for processing input images.
    /// </summary>
    public decimal? ImageToken { get; init; }

    /// <summary>
    /// Cost per generated output image.
    /// </summary>
    public decimal? ImageOutput { get; init; }

    /// <summary>
    /// Cost per token for input audio.
    /// </summary>
    public decimal? Audio { get; init; }

    /// <summary>
    /// Cost per token for generated output audio.
    /// </summary>
    public decimal? AudioOutput { get; init; }

    /// <summary>
    /// Cost per token for cached input audio.
    /// </summary>
    public decimal? InputAudioCache { get; init; }

    /// <summary>
    /// Cost per web search performed by the model.
    /// </summary>
    public decimal? WebSearch { get; init; }

    /// <summary>
    /// Cost per token for the model's internal reasoning (chain-of-thought).
    /// </summary>
    public decimal? InternalReasoning { get; init; }

    /// <summary>
    /// Cost per token for reading from the prompt cache (cache hit).
    /// </summary>
    public decimal? InputCacheRead { get; init; }

    /// <summary>
    /// Cost per token for writing to the prompt cache (cache miss).
    /// </summary>
    public decimal? InputCacheWrite { get; init; }

    /// <summary>
    /// Discount multiplier applied to the overall cost (e.g., 0.5 for 50% off).
    /// </summary>
    public double? Discount { get; init; }
}
