namespace Achates.Configuration;

public sealed class AchatesConfig
{
    public string? Provider { get; set; }
    public Dictionary<string, AgentConfig>? Agents { get; set; }
    public Dictionary<string, ChannelConfig>? Channels { get; set; }
    public ConsoleConfig? Console { get; set; }
}

public sealed class AgentConfig
{
    public string? Description { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public List<string>? Tools { get; set; }
    public string? Prompt { get; set; }
    public string? TodoFile { get; set; }
    public CompletionConfig? Completion { get; set; }
    public Dictionary<string, GraphConfig>? Graph { get; set; }
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
    public string? Transport { get; set; }
    public string? Agent { get; set; }
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
    public string? Channel { get; set; }
    public string? Peer { get; set; }
}
