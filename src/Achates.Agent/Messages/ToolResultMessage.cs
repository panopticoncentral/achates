using System.Text.Json.Serialization;
using Achates.Providers.Completions.Content;

namespace Achates.Agent.Messages;

public sealed record ToolResultMessage : AgentMessage
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlyList<CompletionUserContent> Content { get; init; }

    public bool IsError { get; init; }

    /// <summary>
    /// Relative URL for an image produced by this tool (e.g. /agents/{name}/images/{file}).
    /// Persisted in the session so clients can render images when reloading timeline history.
    /// </summary>
    public string? ImageUrl { get; init; }

    [JsonIgnore]
    public object? Details { get; init; }
}
