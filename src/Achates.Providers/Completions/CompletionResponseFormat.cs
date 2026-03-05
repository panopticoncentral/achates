using System.Text.Json;

namespace Achates.Providers.Completions;

public sealed record CompletionResponseFormat
{
    public required string Type { get; init; }
    public JsonElement? JsonSchema { get; init; }
}
