using System.Text.Json;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Events;
using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class ChatToolTests
{
    private static readonly Model StubModel = new()
    {
        Id = "test/model",
        Name = "Test Model",
        Provider = new StubProvider(),
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000,
        Input = ModelModalities.Text,
        Output = ModelModalities.Text,
        Parameters = ModelParameters.Tools,
    };

    private static AgentDefinition MakeAgentDef(string? description = null) => new()
    {
        Model = StubModel,
        SystemPrompt = "You are a test agent.",
        Tools = [],
        CompletionOptions = null,
        MemoryPath = "/tmp/test-memory.md",
    };

    private static Dictionary<string, AgentInfo> MakeRegistry(params (string Name, string? Desc)[] agents)
    {
        var registry = new Dictionary<string, AgentInfo>();
        foreach (var (name, desc) in agents)
        {
            registry[name] = new AgentInfo
            {
                AgentDef = MakeAgentDef(desc),
                Description = desc,
                ToolNames = ["session", "memory"],
            };
        }
        return registry;
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in args)
            dict[key] = value is string s
                ? JsonDocument.Parse($"\"{s}\"").RootElement
                : value;
        dict.TryAdd("action", JsonDocument.Parse("\"chat\"").RootElement);
        return dict;
    }

    // --- ListAgents ---

    [Fact]
    public async Task ListAgents_shows_other_agents()
    {
        var registry = MakeRegistry(("alice", "Research agent"), ("bob", "Writer"), ("self", "Me"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        var text = GetText(result);
        Assert.Contains("alice", text);
        Assert.Contains("Research agent", text);
        Assert.Contains("bob", text);
        Assert.DoesNotContain("**self**", text);
    }

    [Fact]
    public async Task ListAgents_respects_allowlist()
    {
        var registry = MakeRegistry(("alice", "A"), ("bob", "B"), ("self", "Me"));
        var tool = new ChatTool("self", registry, ["alice"]);

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        var text = GetText(result);
        Assert.Contains("alice", text);
        Assert.DoesNotContain("bob", text);
    }

    [Fact]
    public async Task ListAgents_empty_allowlist_shows_all()
    {
        var registry = MakeRegistry(("alice", "A"), ("bob", "B"), ("self", "Me"));
        var tool = new ChatTool("self", registry, []);

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        var text = GetText(result);
        Assert.Contains("alice", text);
        Assert.Contains("bob", text);
    }

    [Fact]
    public async Task ListAgents_no_others_returns_message()
    {
        var registry = MakeRegistry(("self", "Me"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        Assert.Contains("No other agents", GetText(result));
    }

    // --- Chat validation ---

    [Fact]
    public async Task Chat_requires_agent_name()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1", Args(("message", "hello")));

        Assert.Contains("'agent' is required", GetText(result));
    }

    [Fact]
    public async Task Chat_requires_message()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1", Args(("agent", "bob")));

        Assert.Contains("'message' is required", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_self_target()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1", Args(("agent", "self"), ("message", "hello")));

        Assert.Contains("cannot chat with yourself", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_unknown_agent()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1", Args(("agent", "unknown"), ("message", "hello")));

        Assert.Contains("not found", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_disallowed_agent()
    {
        var registry = MakeRegistry(("self", "Me"), ("alice", "A"), ("bob", "B"));
        var tool = new ChatTool("self", registry, ["alice"]);

        var result = await tool.ExecuteAsync("1", Args(("agent", "bob"), ("message", "hello")));

        Assert.Contains("not allowed", GetText(result));
    }

    [Fact]
    public async Task ListAgents_shows_tool_names()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null);

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        var text = GetText(result);
        Assert.Contains("session", text);
        Assert.Contains("memory", text);
    }

    // --- Helpers ---

    private static string GetText(Agent.Tools.AgentToolResult result) =>
        string.Join("", result.Content.OfType<Providers.Completions.Content.CompletionTextContent>()
            .Select(c => c.Text));

    private sealed class StubProvider : IModelProvider
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string EnvironmentKey => "STUB_KEY";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
