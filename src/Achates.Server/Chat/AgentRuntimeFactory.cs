using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Models;

namespace Achates.Server.Chat;

/// <summary>
/// Builds a target <see cref="AgentRuntime"/> for one chat round, seeded with a
/// reconstructed message history. Injectable so tests can supply a stub model.
/// Carries the target agent's cost ledger so the round's usage is recorded.
/// Carries a precomputed universal-tools list (memory + cost) so the consulted
/// agent has the same always-on tools it would have in a normal session.
///
/// Core memory is injected; working memory deliberately is not. A one-round
/// consult has no "next time we talk", and injecting user-directed intentions
/// risks the agent raising them with another agent instead.
/// </summary>
public sealed class AgentRuntimeFactory(
    Model model,
    string? systemPrompt = null,
    CostLedger? ledger = null,
    IReadOnlyList<AgentTool>? universalTools = null,
    string? coreMemoryPath = null)
{
    public CostLedger? Ledger { get; } = ledger;

    public AgentRuntime Create(IReadOnlyList<AgentMessage> seed)
    {
        var temporal = TemporalContext.CreateTransform();
        Func<CompletionContext, CompletionContext> transform = temporal;

        if (coreMemoryPath is not null)
        {
            var memory = MemoryContext.CreateTransform(
                coreMemoryPath, workingPath: null, includeWorking: false);
            transform = ctx => memory(temporal(ctx));
        }

        return new AgentRuntime(new AgentOptions
        {
            Model = model,
            SystemPrompt = systemPrompt,
            Tools = universalTools,
            Messages = seed,
            TransformContext = transform,
        });
    }
}
