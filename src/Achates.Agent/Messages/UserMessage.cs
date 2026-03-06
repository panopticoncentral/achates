using Achates.Providers.Completions.Content;

namespace Achates.Agent.Messages;

public sealed record UserMessage : AgentMessage
{
    public required string Text { get; init; }

    /// <summary>
    /// Optional additional content blocks (images, files, audio) sent alongside the text.
    /// </summary>
    public IReadOnlyList<CompletionUserContent>? Content { get; init; }
}
