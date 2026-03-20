namespace Achates.Server;

public sealed class AchatesConfig
{
    public ProviderConfig? Provider { get; set; }
    public ToolsConfig? Tools { get; set; }
}

public sealed class AgentConfig
{
    public string? Description { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public List<string>? Tools { get; set; }
    public CompletionConfig? Completion { get; set; }

    /// <summary>
    /// Allowlist of agent names this agent can chat with via the chat tool.
    /// If null or empty when chat tool is enabled, all other agents are allowed.
    /// </summary>
    public List<string>? AllowChat { get; set; }

    /// <summary>
    /// System prompt from the ## Prompt section of AGENT.md.
    /// Set by <see cref="AgentLoader"/>, not deserialized.
    /// </summary>
    public string? Prompt { get; set; }
}

public sealed class ToolsConfig
{
    public TodoConfig? Todo { get; set; }
    public NotesConfig? Notes { get; set; }
    public WebSearchConfig? WebSearch { get; set; }
    public TranscribeConfig? Transcribe { get; set; }
    public Dictionary<string, GraphConfig>? Graph { get; set; }
    public WithingsConfig? Withings { get; set; }
}

public sealed class TranscribeConfig
{
    public string? Model { get; set; }
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

public sealed class CompletionConfig
{
    public string? ReasoningEffort { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class ProviderConfig
{
    public string? Name { get; set; }
    public string? ApiKey { get; set; }
}
