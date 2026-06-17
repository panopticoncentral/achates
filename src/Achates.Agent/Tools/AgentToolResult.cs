using Achates.Providers.Completions.Content;

namespace Achates.Agent.Tools;

public sealed record AgentToolResult
{
    /// <summary>
    /// Content to send back to the model as the tool's result.
    /// </summary>
    public required IReadOnlyList<CompletionUserContent> Content { get; init; }

    /// <summary>
    /// Relative URL for an image produced by this tool result.
    /// Propagated to the <see cref="Messages.ToolResultMessage"/> for session persistence.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Optional metadata for the UI or logging (not sent to the model).
    /// </summary>
    public object? Details { get; init; }

    /// <summary>
    /// Content to splice in as a follow-up user message after the tool result,
    /// rather than into the tool-result message itself. Used to deliver PDFs,
    /// which the provider cannot serialize inside a tool-result message.
    /// </summary>
    public IReadOnlyList<CompletionUserContent>? InjectedUserContent { get; init; }
}
