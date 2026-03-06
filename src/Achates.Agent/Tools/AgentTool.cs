using System.Text.Json;
using Achates.Providers.Completions;

namespace Achates.Agent.Tools;

/// <summary>
/// Base class for tools that an agent can invoke.
/// Implement <see cref="ExecuteAsync"/> to define the tool's behavior.
/// </summary>
public abstract class AgentTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>
    /// Human-readable label for display in UI.
    /// </summary>
    public virtual string Label => Name;

    /// <summary>
    /// JSON Schema describing the tool's parameters.
    /// </summary>
    public abstract JsonElement Parameters { get; }

    /// <summary>
    /// Execute the tool with the given arguments.
    /// </summary>
    /// <param name="toolCallId">Unique ID for this invocation.</param>
    /// <param name="arguments">Parsed arguments matching <see cref="Parameters"/> schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onProgress">Optional callback for streaming partial results during execution.</param>
    public abstract Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null);

    /// <summary>
    /// Converts this tool to a provider-level <see cref="CompletionTool"/>.
    /// </summary>
    public CompletionTool ToCompletionTool() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = Parameters,
    };
}
