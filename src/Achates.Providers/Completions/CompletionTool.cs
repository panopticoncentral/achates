using System.Text.Json;

namespace Achates.Providers.Completions;

public sealed record CompletionTool
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema object describing the tool's parameters.
    /// </summary>
    public required JsonElement Parameters { get; init; }
}
