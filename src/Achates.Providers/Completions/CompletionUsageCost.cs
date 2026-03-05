namespace Achates.Providers.Completions;

public sealed record CompletionUsageCost
{
    public decimal Input { get; init; }

    public decimal Output { get; init; }

    public decimal CacheRead { get; init; }

    public decimal CacheWrite { get; init; }

    public decimal Total { get; init; }
}
