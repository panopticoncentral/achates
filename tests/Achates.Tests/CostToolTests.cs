using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class CostToolTests : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, CostLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);

    public CostToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"achates-cost-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private CostLedger Ledger(string agentName)
    {
        if (!_ledgers.TryGetValue(agentName, out var ledger))
        {
            var path = Path.Combine(_root, agentName, "costs.jsonl");
            ledger = new CostLedger(path);
            _ledgers[agentName] = ledger;
        }
        return ledger;
    }

    private CostTool CreateTool(string callingAgent = "alpha")
    {
        // Ensure caller is present even if it has no entries.
        _ = Ledger(callingAgent);
        return new CostTool(callingAgent, _ledgers);
    }

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) => JsonDocument.Parse($"\"{s}\"").RootElement;

    private static string Text(AgentToolResult result) =>
        ((CompletionTextContent)result.Content[0]).Text;

    private static CostEntry Entry(
        string model,
        string channel,
        string peer,
        decimal cost,
        int inputTokens = 100,
        int outputTokens = 50,
        int cacheReadTokens = 0,
        int cacheWriteTokens = 0,
        DateTimeOffset? when = null) =>
        new()
        {
            Timestamp = when ?? DateTimeOffset.UtcNow,
            Model = model,
            Channel = channel,
            Peer = peer,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            CostTotal = cost,
            CostInput = cost * 0.4m,
            CostOutput = cost * 0.6m,
        };

    // -- scope=self (default / backwards-compatible) -------------------------

    [Fact]
    public async Task Summary_NoScope_DefaultsToSelf()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("beta").AppendAsync(Entry("gpt-5", "beta", "shared", 0.50m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("period", JE("all"))));

        var text = Text(result);
        // Alpha's $0.10 should show; beta's $0.50 must NOT leak in.
        Assert.Contains("$0.1000", text);
        Assert.DoesNotContain("$0.5000", text);
        Assert.DoesNotContain("$0.6000", text);
    }

    [Fact]
    public async Task Summary_Self_ProducesSameShapeAsBefore()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m, 200, 100));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("All time", text);
        Assert.Contains("$0.1000", text);
        Assert.Contains("Completions: 1", text);
        Assert.Contains("Input tokens: 200", text);
        Assert.Contains("Output tokens: 100", text);
    }

    // -- scope=all (cross-agent) ---------------------------------------------

    [Fact]
    public async Task Summary_All_AggregatesAcrossAgents()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("beta").AppendAsync(Entry("gpt-5", "beta", "shared", 0.50m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("scope", JE("all")),
            ("period", JE("all"))));

        var text = Text(result);
        // Grand total $0.60 = $0.10 + $0.50
        Assert.Contains("$0.6000", text);
        // Per-agent rows must surface both agent names.
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    [Fact]
    public async Task Summary_All_NoEntriesAnywhere_ReportsClearly()
    {
        _ = Ledger("alpha");
        _ = Ledger("beta");

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("scope", JE("all")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("No usage", text, StringComparison.OrdinalIgnoreCase);
    }

    // -- scope=<agent-name> --------------------------------------------------

    [Fact]
    public async Task Summary_NamedAgent_ShowsOnlyThatAgent()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("beta").AppendAsync(Entry("gpt-5", "beta", "shared", 0.50m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("scope", JE("beta")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("$0.5000", text);
        Assert.DoesNotContain("$0.1000", text);
    }

    [Fact]
    public async Task Summary_UnknownAgent_ReturnsFriendlyError()
    {
        _ = Ledger("alpha");
        _ = Ledger("beta");

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("scope", JE("gamma")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("Unknown agent", text);
        Assert.Contains("gamma", text);
        // Suggests the available names.
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    // -- breakdown by new dimensions ------------------------------------------

    [Fact]
    public async Task Breakdown_ByAgent_All_GroupsCorrectly()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.05m));
        await Ledger("beta").AppendAsync(Entry("gpt-5", "beta", "shared", 0.50m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("breakdown")),
            ("scope", JE("all")),
            ("group_by", JE("agent")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("alpha", text);
        Assert.Contains("$0.1500", text); // alpha's two entries summed
        Assert.Contains("beta", text);
        Assert.Contains("$0.5000", text);
        Assert.Contains("$0.6500", text); // grand total
    }

    [Fact]
    public async Task Breakdown_ByChannel_GroupsByChannel()
    {
        // Direct turn (channel=agentName), chat (channel="chat"), cron (channel="cron")
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "chat", "beta", 0.20m));
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "cron", "job-1", 0.30m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("breakdown")),
            ("group_by", JE("channel")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("alpha", text);  // direct channel
        Assert.Contains("chat", text);
        Assert.Contains("cron", text);
        Assert.Contains("$0.1000", text);
        Assert.Contains("$0.2000", text);
        Assert.Contains("$0.3000", text);
        Assert.Contains("$0.6000", text); // grand total
    }

    [Fact]
    public async Task Breakdown_ByPeer_GroupsByPeer()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "chat", "beta", 0.10m));
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "chat", "beta", 0.20m));
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "cron", "job-x", 0.40m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("breakdown")),
            ("group_by", JE("peer")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("beta", text);
        Assert.Contains("job-x", text);
        Assert.Contains("$0.3000", text); // beta sum
        Assert.Contains("$0.4000", text); // job-x sum
    }

    [Fact]
    public async Task Breakdown_ByDay_StillWorks_BackwardsCompatible()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("breakdown")),
            ("group_by", JE("day")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("$0.1000", text);
        Assert.Contains("Total", text);
    }

    // -- new fields surfacing ------------------------------------------------

    [Fact]
    public async Task Summary_CacheWriteTokens_AppearsWhenNonZero()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m, cacheWriteTokens: 500));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("period", JE("all"))));

        Assert.Contains("Cache write tokens: 500", Text(result));
    }

    [Fact]
    public async Task Summary_CacheWriteTokens_HiddenWhenZero()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("summary")),
            ("period", JE("all"))));

        Assert.DoesNotContain("Cache write tokens", Text(result));
    }

    [Fact]
    public async Task Recent_IncludesChannelColumn()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "chat", "beta", 0.20m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("recent")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("chat", text);
        Assert.Contains("$0.2000", text);
    }

    [Fact]
    public async Task Recent_All_PrefixesAgentName()
    {
        await Ledger("alpha").AppendAsync(Entry("gpt-5", "alpha", "shared", 0.10m));
        await Ledger("beta").AppendAsync(Entry("gpt-5", "beta", "shared", 0.50m));

        var tool = CreateTool("alpha");
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("recent")),
            ("scope", JE("all")),
            ("period", JE("all"))));

        var text = Text(result);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }
}
