using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Models;

namespace Achates.Server.Chat;

/// <summary>
/// Builds a target <see cref="AgentRuntime"/> for one chat round, seeded with a
/// reconstructed message history. Injectable so tests can supply a stub model.
/// Carries the target agent's cost ledger so the round's usage is recorded.
/// </summary>
public sealed class AgentRuntimeFactory(Model model, string? systemPrompt = null, CostLedger? ledger = null)
{
    public CostLedger? Ledger { get; } = ledger;

    public AgentRuntime Create(IReadOnlyList<AgentMessage> seed) => new(new AgentOptions
    {
        Model = model,
        SystemPrompt = systemPrompt,
        Messages = seed,
    });
}
