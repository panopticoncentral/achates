namespace Achates.Server;

public sealed class AchatesConfig
{
    public ProviderConfig? Provider { get; set; }
    public ModelsConfig? Models { get; set; }
    public ToolsConfig? Tools { get; set; }
    public CronConfig? Cron { get; set; }
}

public sealed class ModelsConfig
{
    /// <summary>
    /// Default model used by all agents.
    /// </summary>
    public string? Base { get; set; }

    /// <summary>
    /// Model used by the think tool for escalated reasoning.
    /// </summary>
    public string? Thinking { get; set; }
}

public sealed class CronConfig
{
    /// <summary>
    /// Number of most-recent sessions to keep per cron job. Older sessions
    /// for the same job are pruned by the reaper. Default 1. Set to 0 to
    /// disable retention-based pruning.
    /// </summary>
    public int? KeepLastPerJob { get; set; }

    /// <summary>
    /// Absolute ceiling in days — any cron-origin session older than this is
    /// pruned regardless of <see cref="KeepLastPerJob"/>. Null disables the
    /// absolute ceiling. Default 30.
    /// </summary>
    public int? MaxAgeDays { get; set; }
}

public sealed class AgentConfig
{
    /// <summary>
    /// Display name from the H1 title of AGENT.md. Set by <see cref="AgentLoader"/>.
    /// </summary>
    public string? Title { get; set; }

    public string? Description { get; set; }
    public string? Provider { get; set; }

    /// <summary>
    /// Per-agent base model id (e.g. "anthropic/claude-sonnet-4.6"). Overrides the
    /// global <see cref="ModelsConfig.Base"/>. Null/empty falls back to the global.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Per-agent thinking model id used by the think tool. Overrides the global
    /// <see cref="ModelsConfig.Thinking"/>. Null/empty falls back to the global.
    /// Only consulted when the agent has the <c>think</c> tool.
    /// </summary>
    public string? ThinkingModel { get; set; }

    /// <summary>
    /// Per-agent voice id for TTS (e.g. "af_nicole" or a Kokoro blend like
    /// "af_nicole(0.7)+af_bella(0.3)"). Null/empty means the agent is
    /// voiceless — speech is not generated even when the per-session toggle
    /// is on, unless <c>tools.speech.default_voice</c> is set globally.
    /// </summary>
    public string? Voice { get; set; }

    /// <summary>
    /// Per-agent TTS rate. <c>1.0</c> is normal speed; Kokoro accepts the
    /// inclusive range <c>[0.25, 4.0]</c> and rejects values outside it.
    /// Null means "use Kokoro's default (1.0)" — the field is then omitted
    /// from the synthesis request body. Out-of-range values supplied via
    /// AGENT.md are clamped to the accepted range silently.
    /// </summary>
    public double? SpeechRate { get; set; }

    public List<string>? Tools { get; set; }
    public CompletionConfig? Completion { get; set; }

    /// <summary>
    /// Allowlist of agent names this agent can chat with via the chat tool.
    /// If null or empty when chat tool is enabled, all other agents are allowed.
    /// </summary>
    public List<string>? AllowChat { get; set; }

    /// <summary>
    /// Local time of day for nightly dreamtime (memory consolidation).
    /// Parsed from "HH:mm" or "h:mm AM/PM" format. Null means dreamtime is disabled.
    /// </summary>
    public TimeOnly? Dreamtime { get; set; }

    /// <summary>
    /// Whether the agent may access the shared memory scope (universal user facts at
    /// <c>~/.achates/memory.md</c>). Null means "not specified" — resolves to the
    /// default of <c>true</c>. Setting this to <c>false</c> hides the shared scope
    /// from <see cref="Tools.MemoryTool"/>'s schema so the model never sees it —
    /// useful for roleplay/in-character agents that should not be polluted by
    /// real-world identity facts.
    /// </summary>
    public bool? SharedMemory { get; set; }

    /// <summary>
    /// System prompt from the ## Prompt section of AGENT.md.
    /// Set by <see cref="AgentLoader"/>, not deserialized.
    /// </summary>
    public string? Prompt { get; set; }
}

public sealed class ToolsConfig
{
    public NotebookConfig? Notebook { get; set; }
    public WebSearchConfig? WebSearch { get; set; }
    public TranscribeConfig? Transcribe { get; set; }
    public AvatarConfig? Avatar { get; set; }
    public ImageConfig? Image { get; set; }
    public TitleConfig? Title { get; set; }
    public Dictionary<string, GraphConfig>? Graph { get; set; }
    public WithingsConfig? Withings { get; set; }
    public Achates.Server.Speech.SpeechConfig? Speech { get; set; }
}

public sealed class AvatarConfig
{
    public string? Model { get; set; }
}

public sealed class ImageConfig
{
    /// <summary>
    /// Single image model id. Legacy single-model form.
    /// Use <see cref="Models"/> to expose a choice to the agent.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// List of image model ids the agent can choose from. When set (and
    /// non-empty), takes precedence over <see cref="Model"/>.
    /// </summary>
    public List<string>? Models { get; set; }

    /// <summary>
    /// Optional override API key used for image generation (both the
    /// <c>image</c> tool and avatar generation). Falls back to
    /// <see cref="ProviderConfig.ApiKey"/> when null/empty. Useful for routing
    /// image traffic through a separate (non-ZDR) key while keeping chat
    /// traffic on a privacy-restricted key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Effective list of model ids: <see cref="Models"/> if non-empty,
    /// otherwise a single-element list from <see cref="Model"/>, otherwise empty.
    /// </summary>
    public IReadOnlyList<string> ResolveModels()
    {
        if (Models is { Count: > 0 })
            return [.. Models.Where(m => !string.IsNullOrWhiteSpace(m))];
        if (!string.IsNullOrWhiteSpace(Model))
            return [Model];
        return [];
    }
}

public sealed class TranscribeConfig
{
    public string? Model { get; set; }
}

public sealed class TitleConfig
{
    public string? Model { get; set; }
}

public sealed class NotebookConfig
{
    public string? Root { get; set; }
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
