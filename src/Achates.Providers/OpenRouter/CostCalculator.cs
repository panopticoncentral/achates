using System.Globalization;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;

namespace Achates.Providers.OpenRouter;

/// <summary>
/// Breakdown of costs for a single API call, in USD.
/// </summary>
public sealed record UsageCost
{
    /// <summary>Cost for prompt/input tokens.</summary>
    public decimal Input { get; init; }

    /// <summary>Cost for completion/output tokens.</summary>
    public decimal Output { get; init; }

    /// <summary>Total cost (Input + Output).</summary>
    public decimal Total { get; init; }
}

/// <summary>
/// Calculates the dollar cost of a chat completion from token usage and model pricing.
/// </summary>
public static class CostCalculator
{
    /// <summary>
    /// Calculates cost given usage data and the model's pricing.
    /// Returns null if pricing data is unavailable or unparseable.
    /// </summary>
    public static UsageCost? Calculate(ChatUsage? usage, OpenRouterPricing? pricing)
    {
        if (usage is null || pricing is null)
        {
            return null;
        }

        // OpenRouter pricing strings are per-token rates (e.g. "0.000003" = $3/M tokens).
        var inputCost = ComputeCost(pricing.Prompt, usage.PromptTokens);
        var outputCost = ComputeCost(pricing.Completion, usage.CompletionTokens);

        if (inputCost is null && outputCost is null)
        {
            return null;
        }

        var input = inputCost ?? 0m;
        var output = outputCost ?? 0m;

        return new UsageCost
        {
            Input = input,
            Output = output,
            Total = input + output
        };
    }

    private static decimal? ComputeCost(string? perTokenRate, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(perTokenRate))
        {
            return null;
        }

        if (!decimal.TryParse(perTokenRate, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rate))
        {
            return null;
        }

        return rate * tokenCount;
    }
}
