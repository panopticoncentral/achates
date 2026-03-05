using Achates.Providers.Models;

namespace Achates.Providers.Completions;

public sealed record CompletionUsage
{
    public int Input { get; init; }

    public int Output { get; init; }

    public int CacheRead { get; init; }

    public int CacheWrite { get; init; }

    public int TotalTokens { get; init; }

    public required CompletionUsageCost Cost { get; init; }

    public static CompletionUsage Empty => new()
    {
        Cost = new CompletionUsageCost(),
    };

    /// <summary>
    /// Calculate the cost for a given model and usage.
    /// </summary>
    public CompletionUsageCost CalculateCost(Model model)
    {
        var inputCost = model.Cost.Prompt * Input;
        var outputCost = model.Cost.Completion * Output;
        var cacheReadCost = (model.Cost.InputCacheRead ?? 0m) * CacheRead;
        var cacheWriteCost = (model.Cost.InputCacheWrite ?? 0m) * CacheWrite;

        return new CompletionUsageCost
        {
            Input = inputCost,
            Output = outputCost,
            CacheRead = cacheReadCost,
            CacheWrite = cacheWriteCost,
            Total = inputCost + outputCost + cacheReadCost + cacheWriteCost,
        };
    }
}
