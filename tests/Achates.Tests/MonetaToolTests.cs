using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class MonetaToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"achates-moneta-{Guid.NewGuid():N}");
    private readonly string _agentFile;
    private readonly string _hash;
    private MonetaConfig? _config;
    private int _calls;

    public MonetaToolTests()
    {
        Directory.CreateDirectory(_root);
        _agentFile = Path.Combine(_root, "AGENT.md");
        File.WriteAllText(_agentFile, "# Finance\n\n## Capabilities\n\n**Tools:** moneta\n\n## Prompt\nRead my finances.");
        _hash = MonetaAccess.DefinitionHash(_agentFile);
        _config = new MonetaConfig
        {
            Executable = "/synthetic/moneta-read", Database = "/synthetic/ledger.db",
            ApprovedAgents = new() { ["finance"] = _hash },
        };
    }

    private MonetaTool Tool(string agent = "finance", string? loadedHash = null,
        Func<string, string, string, CancellationToken, Task<string>>? run = null) =>
        new(agent, _agentFile, loadedHash ?? _hash, () => _config, run ?? ((exe, db, request, ct) =>
        {
            _calls++;
            Assert.Equal("/synthetic/moneta-read", exe);
            Assert.Equal("/synthetic/ledger.db", db);
            Assert.Contains("accounts", request);
            return Task.FromResult("{\"protocol_version\":1,\"rows\":[]}");
        }));

    private static Dictionary<string, object?> Args() => new() { ["action"] = "accounts" };
    private static string Text(Achates.Agent.Tools.AgentToolResult result) => ((CompletionTextContent)result.Content[0]).Text;

    [Fact]
    public async Task Approved_agent_uses_fixed_paths_and_returns_json()
    {
        var response = Text(await Tool().ExecuteAsync("t", Args()));
        Assert.Contains("rows", response);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Missing_approval_and_other_agents_cannot_launch_cli()
    {
        Assert.Contains("access_denied", Text(await Tool("other").ExecuteAsync("t", Args())));
        _config!.ApprovedAgents!.Clear();
        Assert.Contains("access_denied", Text(await Tool().ExecuteAsync("t", Args())));
        _config = null;
        Assert.Contains("access_denied", Text(await Tool().ExecuteAsync("t", Args())));
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task Existing_tool_observes_revocation_on_next_request()
    {
        var tool = Tool();
        await tool.ExecuteAsync("t", Args());
        _config!.ApprovedAgents!.Clear();
        Assert.Contains("access_denied", Text(await tool.ExecuteAsync("t2", Args())));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Edited_or_stale_definitions_fail_even_if_tool_is_assigned()
    {
        Assert.Equal(_hash, AgentLoader.Parse(File.ReadAllText(_agentFile))!.DefinitionHash);
        File.AppendAllText(_agentFile, "\nModified instructions.");
        Assert.Contains("access_denied", Text(await Tool().ExecuteAsync("t", Args())));
        var newHash = MonetaAccess.DefinitionHash(_agentFile);
        _config!.ApprovedAgents!["finance"] = newHash;
        // A stale runtime must not inherit approval of a newer definition.
        Assert.Contains("access_denied", Text(await Tool().ExecuteAsync("t", Args())));
        Assert.Contains("rows", Text(await Tool(loadedHash: newHash).ExecuteAsync("t", Args())));
        Assert.Equal(1, _calls);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("executable")]
    [InlineData("sql")]
    [InlineData("approved_agents")]
    public async Task Agent_cannot_supply_paths_sql_or_approval(string field)
    {
        var args = Args(); args[field] = "anything";
        Assert.Contains("invalid_request", Text(await Tool().ExecuteAsync("t", args)));
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task Revocation_during_query_withholds_result()
    {
        var tool = Tool(run: (exe, db, json, ct) =>
        {
            _config!.ApprovedAgents!.Clear();
            return Task.FromResult("private synthetic result");
        });
        var result = Text(await tool.ExecuteAsync("t", Args()));
        Assert.Contains("access_denied", result);
        Assert.DoesNotContain("private synthetic result", result);
    }

    [Fact]
    public async Task Malformed_config_and_process_failures_do_not_expose_secrets()
    {
        var tool = Tool(run: (_, _, _, _) => throw new IOException("sensitive path and data"));
        var result = Text(await tool.ExecuteAsync("t", Args()));
        Assert.Contains("query_failed", result);
        Assert.DoesNotContain("sensitive path", result);
        var configPath = Path.Combine(_root, "config.yaml");
        File.WriteAllText(configPath, "tools: [broken");
        tool = new MonetaTool("finance", _agentFile, _hash, () => MonetaAccess.ReadConfig(configPath));
        Assert.Contains("query_failed", Text(await tool.ExecuteAsync("t", Args())));
    }

    [Fact]
    public void Owner_config_roundtrips_grants_and_missing_file_is_not_created()
    {
        var path = Path.Combine(_root, "config.yaml");
        ConfigLoader.Save(new AchatesConfig { Tools = new ToolsConfig { Moneta = _config } }, path);
        var loaded = MonetaAccess.ReadConfig(path);
        Assert.True(MonetaAccess.IsApproved(loaded, "finance", _agentFile, _hash));
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => MonetaAccess.ReadConfig(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Tool_is_opt_in_and_not_universal()
    {
        Assert.Contains(GatewayService.AllTools, t => t.Name == "moneta");
        // The chat-target runtime receives only UniversalTools, not the target's assigned tools.
        var tools = UniversalTools.Build("finance", new AgentDefinition
        {
            DisplayName = "finance", SystemPrompt = "", CompletionOptions = null,
            WorkingMemoryPath = Path.Combine(_root, "working.md"), Model = new Model
            {
                Id = "test", Name = "test", Provider = null!, Cost = new ModelCost { Prompt = 0, Completion = 0 },
                ContextWindow = 128_000, Input = ModelModalities.Text, Output = ModelModalities.Text,
                Parameters = ModelParameters.Tools,
            }, Tools = [Tool()], ToolNames = ["moneta"], MemoryPath = Path.Combine(_root, "memory.md"),
        }, Path.Combine(_root, "shared.md"), new Dictionary<string, CostLedger>());
        Assert.DoesNotContain(tools, tool => tool.Name == "moneta");
    }

    [Fact]
    public void Financial_chats_require_both_agents_approved_in_both_directions()
    {
        AgentDefinition Definition(params Achates.Agent.Tools.AgentTool[] tools) => new()
        {
            SystemPrompt = "", CompletionOptions = null, MemoryPath = "unused", WorkingMemoryPath = "unused",
            Model = new Model
            {
                Id = "test", Name = "test", Provider = null!, Cost = new ModelCost { Prompt = 0, Completion = 0 },
                ContextWindow = 1000, Input = ModelModalities.Text, Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
            }, Tools = tools, ToolNames = tools.Select(t => t.Name).ToList(),
        };
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["finance"] = Definition(Tool()), ["other"] = Definition(Tool("other")),
            ["ordinary"] = Definition(), ["ordinary2"] = Definition(),
        };
        Assert.False(MonetaAccess.CanChat("other", "finance", agents, () => _config));
        Assert.False(MonetaAccess.CanChat("finance", "other", agents, () => _config));
        Assert.True(MonetaAccess.CanChat("ordinary", "ordinary2", agents, () => _config));
        _config!.ApprovedAgents!["other"] = _hash;
        Assert.True(MonetaAccess.CanChat("other", "finance", agents, () => _config));
        // Removing the assigned tool cannot evade protection of a granted identity.
        agents["finance"] = Definition();
        Assert.False(MonetaAccess.CanChat("ordinary", "finance", agents, () => _config));
        Assert.False(MonetaAccess.CanChat("other", "finance", agents, () => throw new IOException()));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
