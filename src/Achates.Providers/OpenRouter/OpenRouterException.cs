using System.Text.Json;

namespace Achates.Providers.OpenRouter;

internal sealed class OpenRouterException(
    string message,
    int code,
    JsonElement? metadata = null) : Exception(message)
{
    public int Code { get; } = code;

    public JsonElement? Metadata { get; } = metadata;
}

/// <summary>
/// Thrown when an in-progress SSE stream delivers no data for longer than
/// <see cref="OpenRouterClient.StreamIdleTimeout"/>. This converts a silent
/// upstream stall (which OpenRouter would otherwise leave hanging for minutes
/// before reporting) into a prompt, retryable signal.
/// </summary>
internal sealed class StreamIdleTimeoutException(TimeSpan idle)
    : Exception($"Upstream stream idle for {idle.TotalSeconds:0}s with no data.")
{
    public TimeSpan Idle { get; } = idle;
}
