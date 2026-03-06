using Achates.Providers.Completions.Content;

namespace Achates.Agent.Tools;

public sealed record AgentToolResult
{
    /// <summary>
    /// Content to send back to the model as the tool's result.
    /// </summary>
    public required IReadOnlyList<CompletionUserContent> Content { get; init; }

    /// <summary>
    /// Optional metadata for the UI or logging (not sent to the model).
    /// </summary>
    public object? Details { get; init; }
}
