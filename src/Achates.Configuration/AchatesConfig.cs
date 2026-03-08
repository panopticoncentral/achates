namespace Achates.Configuration;

public sealed class AchatesConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public CompletionConfig? Completion { get; set; }
    public ConsoleConfig? Console { get; set; }
    public TelegramConfig? Telegram { get; set; }
}

public sealed class CompletionConfig
{
    public string? ReasoningEffort { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class TelegramConfig
{
    public string? Token { get; set; }
    public long[]? AllowedChatIds { get; set; }
}

public sealed class ConsoleConfig
{
    public string? Url { get; set; }
    public string? Channel { get; set; }
    public string? Peer { get; set; }
}
