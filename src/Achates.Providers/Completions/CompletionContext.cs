using Achates.Providers.Completions.Messages;

namespace Achates.Providers.Completions;

public sealed record CompletionContext
{
    public string? SystemPrompt { get; init; }

    public required IReadOnlyList<CompletionMessage> Messages { get; init; }

    public IReadOnlyList<CompletionTool>? Tools { get; init; }
}
