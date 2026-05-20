using Achates.Agent.Tools;

namespace Achates.Server.Tools;

/// <summary>
/// Builds the set of tools every agent runtime always has, regardless of the
/// agent's configured tool list. Currently: <see cref="MemoryTool"/> (always)
/// and <see cref="CostTool"/> (when at least one cost ledger is in scope).
///
/// Single source of truth — used by <c>MobileTransport.CreateRuntime</c>,
/// <c>CronService.BuildJobTools</c>, <c>CronService.BuildDreamtimeTools</c>,
/// and the chat-target factory <c>AgentRuntimeFactory</c>.
/// </summary>
internal static class UniversalTools
{
    public static IReadOnlyList<AgentTool> Build(
        string agentName,
        AgentDefinition agentDef,
        string sharedMemoryPath,
        IReadOnlyDictionary<string, CostLedger> costLedgers)
    {
        var tools = new List<AgentTool>
        {
            new MemoryTool(sharedMemoryPath, agentDef.MemoryPath),
        };
        if (costLedgers.Count > 0)
            tools.Add(new CostTool(agentName, costLedgers));
        return tools;
    }
}
