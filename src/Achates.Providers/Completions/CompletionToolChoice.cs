namespace Achates.Providers.Completions;

public sealed record CompletionToolChoice
{
    public required ToolChoiceType Type { get; init; }
    public required string Name { get; init; }
}
