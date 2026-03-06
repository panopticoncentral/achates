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
