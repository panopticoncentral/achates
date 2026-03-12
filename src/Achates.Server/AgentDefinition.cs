using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Graph;

namespace Achates.Server;

/// <summary>
/// A resolved agent configuration with runtime objects (model, tools, prompt).
/// Created from <see cref="Achates.Configuration.AgentConfig"/> during startup.
/// </summary>
public sealed record AgentDefinition
{
    public required Model Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<AgentTool> Tools { get; init; }
    public required CompletionOptions? CompletionOptions { get; init; }
    public required string MemoryPath { get; init; }
    public string? TodoPath { get; init; }
    public CostLedger? CostLedger { get; init; }
    public IReadOnlyDictionary<string, GraphClient> GraphClients { get; init; } = new Dictionary<string, GraphClient>();
}
