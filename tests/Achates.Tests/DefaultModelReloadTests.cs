using Achates.Server;

namespace Achates.Tests;

public class DefaultModelReloadTests
{
    private static (string, AgentConfig) Agent(string name, string? model = null,
        string? thinkingModel = null, params string[] tools) =>
        (name, new AgentConfig { Model = model, ThinkingModel = thinkingModel, Tools = [.. tools] });

    [Fact]
    public void BaseChange_ReloadsOnlyAgentsWithoutModelOverride()
    {
        var agents = new[]
        {
            Agent("a"),                          // relies on global base -> reload
            Agent("b", model: "x/custom"),       // overrides base -> skip
        };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: true, thinkingChanged: false);

        Assert.Contains("a", result);
        Assert.DoesNotContain("b", result);
    }

    [Fact]
    public void ThinkingChange_ReloadsOnlyThinkAgentsWithoutThinkingOverride()
    {
        var agents = new[]
        {
            Agent("noThink"),                                   // no think tool -> skip
            Agent("thinkGlobal", tools: "think"),               // think + relies on global -> reload
            Agent("thinkOwn", thinkingModel: "x/t", tools: "think"), // overrides -> skip
        };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: false, thinkingChanged: true);

        Assert.Equal(["thinkGlobal"], result.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void BothChanged_AgentReloadedOnce()
    {
        var agents = new[] { Agent("a", tools: "think") }; // qualifies for both

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: true, thinkingChanged: true);

        Assert.Single(result);
        Assert.Contains("a", result);
    }

    [Fact]
    public void NothingChanged_Empty()
    {
        var agents = new[] { Agent("a", tools: "think") };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: false, thinkingChanged: false);

        Assert.Empty(result);
    }
}
