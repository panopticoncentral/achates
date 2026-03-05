namespace Achates.Providers.Completions.Messages;

public sealed record CompletionUserTextMessage : CompletionUserMessage
{
    public required string Text { get; init; }
}
