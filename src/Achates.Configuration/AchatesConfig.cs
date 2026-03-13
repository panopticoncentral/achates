namespace Achates.Configuration;

public sealed class AchatesConfig
{
    public string? Provider { get; set; }
    public ToolsConfig? Tools { get; set; }
    public Dictionary<string, AgentConfig>? Agents { get; set; }
    public ConsoleConfig? Console { get; set; }
}

public sealed class AgentConfig
{
    public string? Description { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public List<string>? Tools { get; set; }
    public string? Prompt { get; set; }
    public CompletionConfig? Completion { get; set; }
    public Dictionary<string, ChannelConfig>? Channels { get; set; }
}

public sealed class ToolsConfig
{
    public TodoConfig? Todo { get; set; }
    public NotesConfig? Notes { get; set; }
    public WebSearchConfig? WebSearch { get; set; }
    public Dictionary<string, GraphConfig>? Graph { get; set; }
    public WithingsConfig? Withings { get; set; }
}

public sealed class TodoConfig
{
    public string? File { get; set; }
}

public sealed class WebSearchConfig
{
    public string? BraveApiKey { get; set; }
}

public sealed class WithingsConfig
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RedirectUri { get; set; }
}

public sealed class NotesConfig
{
    public string? Folder { get; set; }
}

public sealed class GraphConfig
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? UserEmail { get; set; }
}

public sealed class ChannelConfig
{
    public string? Token { get; set; }
    public long[]? AllowedChatIds { get; set; }
}

public sealed class CompletionConfig
{
    public string? ReasoningEffort { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class ConsoleConfig
{
    public string? Url { get; set; }
    public string? Agent { get; set; }
    public string? Peer { get; set; }
}
