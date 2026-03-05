namespace Achates.Providers.Completions;

public enum CompletionStopReason
{
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted,
}
