using Achates.Agent;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Models;

namespace Achates.Server;

public sealed record GatewayOptions
{
    /// <summary>
    /// The model to use for agent conversations.
    /// </summary>
    public required Model Model { get; init; }

    /// <summary>
    /// System prompt applied to all agent sessions.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Tools available to the agent.
    /// </summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }

    /// <summary>
    /// Completion options (temperature, reasoning effort, etc.).
    /// </summary>
    public CompletionOptions? CompletionOptions { get; init; }

    /// <summary>
    /// Optional session store for persisting conversation history across restarts.
    /// </summary>
    public ISessionStore? SessionStore { get; init; }

    /// <summary>
    /// Base directory for per-peer memory files. When set, each session gets a
    /// MemoryTool backed by {MemoryBasePath}/{channelId}/{peerId}.md.
    /// </summary>
    public string? MemoryBasePath { get; init; }
}
