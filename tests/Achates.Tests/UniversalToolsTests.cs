using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class UniversalToolsTests
{
    private static AgentDefinition MakeAgentDef(string memoryPath, bool sharedMemoryEnabled = true) => new()
    {
        DisplayName = "test",
        Description = "",
        SystemPrompt = "",
        Model = new Model
        {
            Id = "test/model", Name = "Test", Provider = null!,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            ContextWindow = 128_000, Input = ModelModalities.Text,
            Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
        },
        Tools = [],
        ToolNames = [],
        MemoryPath = memoryPath,
        CompletionOptions = null,
        SharedMemoryEnabled = sharedMemoryEnabled,
    };

    [Fact]
    public void Build_returns_only_memory_when_no_ledgers()
    {
        var def = MakeAgentDef("/tmp/agent-mem.md");
        var tools = UniversalTools.Build(
            agentName: "test",
            agentDef: def,
            sharedMemoryPath: "/tmp/shared.md",
            costLedgers: new Dictionary<string, CostLedger>());

        Assert.Single(tools);
        Assert.Equal("memory", tools[0].Name);
    }

    [Fact]
    public void Build_returns_memory_and_cost_when_ledgers_present()
    {
        var def = MakeAgentDef("/tmp/agent-mem.md");
        var ledgerDir = Path.Combine(Path.GetTempPath(), "achates-univ-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(ledgerDir);
            var ledger = new CostLedger(Path.Combine(ledgerDir, "costs.jsonl"));

            var tools = UniversalTools.Build(
                agentName: "test",
                agentDef: def,
                sharedMemoryPath: "/tmp/shared.md",
                costLedgers: new Dictionary<string, CostLedger> { ["test"] = ledger });

            Assert.Equal(2, tools.Count);
            Assert.Equal("memory", tools[0].Name);
            Assert.Equal("cost", tools[1].Name);
        }
        finally { if (Directory.Exists(ledgerDir)) Directory.Delete(ledgerDir, true); }
    }

    [Fact]
    public void Build_passes_SharedMemoryEnabled_to_MemoryTool()
    {
        // The schema is the contract: when shared is enabled it must list both
        // scopes; when disabled it must omit the scope parameter entirely.
        var enabledDef = MakeAgentDef("/tmp/a.md", sharedMemoryEnabled: true);
        var enabledTools = UniversalTools.Build("test", enabledDef, "/tmp/s.md",
            new Dictionary<string, CostLedger>());
        var enabledSchema = enabledTools[0].Parameters.GetRawText();
        Assert.Contains("\"shared\"", enabledSchema);
        Assert.Contains("\"scope\"", enabledSchema);

        var disabledDef = MakeAgentDef("/tmp/a.md", sharedMemoryEnabled: false);
        var disabledTools = UniversalTools.Build("test", disabledDef, "/tmp/s.md",
            new Dictionary<string, CostLedger>());
        var disabledSchema = disabledTools[0].Parameters.GetRawText();
        Assert.DoesNotContain("\"shared\"", disabledSchema);
        Assert.DoesNotContain("\"scope\"", disabledSchema);
    }
}
