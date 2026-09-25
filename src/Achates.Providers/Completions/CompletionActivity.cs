namespace Achates.Providers.Completions;

/// <summary>
/// Turn-scoped progress observer, inherited by nested model calls (including tools).
/// Only content events count; HTTP/SSE keepalives never reach this observer.
/// </summary>
public static class CompletionActivity
{
    private static readonly AsyncLocal<Action?> Observer = new();
    internal static Action? Current => Observer.Value;

    public static IDisposable Observe(Action onProgress)
    {
        var previous = Observer.Value;
        Observer.Value = onProgress;
        return new Scope(previous);
    }

    private sealed class Scope(Action? previous) : IDisposable
    {
        public void Dispose() => Observer.Value = previous;
    }
}
