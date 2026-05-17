using System.Text.Json;
using Achates.Agent.Messages;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Chat;
using Achates.Server.Mobile;
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
        dict.TryAdd("action", JsonDocument.Parse("\"ask\"").RootElement);
        return dict;
    }

    // --- ListAgents ---

    [Fact]
    public async Task ListAgents_shows_other_agents()
    {
        var registry = MakeRegistry(("alice", "Research agent"), ("bob", "Writer"), ("self", "Me"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

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
        var tool = new ChatTool("self", registry, ["alice"], manager: null, initiatorSessionId: "s");

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
        var tool = new ChatTool("self", registry, [], manager: null, initiatorSessionId: "s");

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
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1",
            new Dictionary<string, object?> { ["action"] = JsonDocument.Parse("\"agents\"").RootElement });

        Assert.Contains("No other agents", GetText(result));
    }

    // --- Chat validation ---

    [Fact]
    public async Task Chat_requires_agent_name()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1", Args(("message", "hello")));

        Assert.Contains("'agent' is required", GetText(result));
    }

    [Fact]
    public async Task Chat_requires_message()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1", Args(("agent", "bob")));

        Assert.Contains("'message' is required", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_self_target()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1", Args(("agent", "self"), ("message", "hello")));

        Assert.Contains("cannot chat with yourself", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_unknown_agent()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1", Args(("agent", "unknown"), ("message", "hello")));

        Assert.Contains("not found", GetText(result));
    }

    [Fact]
    public async Task Chat_rejects_disallowed_agent()
    {
        var registry = MakeRegistry(("self", "Me"), ("alice", "A"), ("bob", "B"));
        var tool = new ChatTool("self", registry, ["alice"], manager: null, initiatorSessionId: "s");

        var result = await tool.ExecuteAsync("1", Args(("agent", "bob"), ("message", "hello")));

        Assert.Contains("not allowed", GetText(result));
    }

    [Fact]
    public async Task ListAgents_shows_tool_names()
    {
        var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
        var tool = new ChatTool("self", registry, null, manager: null, initiatorSessionId: "s");

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
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? outputModalities = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Model>>([]);

        // Emits a single assistant turn ending with the done sentinel so an
        // inter-agent chat completes after exactly one exchange.
        public CompletionEventStream GetCompletions(Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default)
            => CompletionEventStream.Create(stream =>
            {
                const string text = "Acknowledged. <<DONE>>";
                var message = new CompletionAssistantMessage
                {
                    Content = [new CompletionTextContent { Text = text }],
                    Model = model.Id,
                    CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                    CompletionStopReason = CompletionStopReason.Stop,
                };
                stream.Push(new CompletionTextDeltaEvent
                {
                    ContentIndex = 0,
                    Delta = text,
                    Partial = message,
                });
                stream.Push(new CompletionDoneEvent
                {
                    Reason = CompletionStopReason.Stop,
                    CompletionMessage = message,
                });
                stream.End();
                return Task.CompletedTask;
            });
    }

    // --- Ask via ChatRoomManager ---

    [Fact]
    public async Task Ask_action_returns_target_reply_via_manager()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "achates-ct-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new Achates.Server.Mobile.MobileSessionStore(baseDir);
            var model = new Model
            {
                Id = "t/m", Name = "t", Provider = new ReplyOnce("the answer"),
                Cost = new ModelCost { Prompt = 0, Completion = 0 }, ContextWindow = 1000,
                Input = ModelModalities.Text, Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
            };
            var mgr = new Achates.Server.Chat.ChatRoomManager(
                store, _ => new Achates.Server.Chat.AgentRuntimeFactory(model));
            var registry = MakeRegistry(("self", "Me"), ("bob", "B"));
            var tool = new ChatTool("self", registry, null, mgr, "sess-1");
            Achates.Server.Tools.ChatSinkAccessor.Current = new AskFakeSink();

            var result = await tool.ExecuteAsync("tc-1",
                Args(("agent", "bob"), ("message", "hello")));

            Assert.Contains("the answer", GetText(result));
        }
        finally
        {
            Achates.Server.Tools.ChatSinkAccessor.Current = null;
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }

    private sealed class AskFakeSink : IChatSink
    {
        public List<string> Events { get; } = [];
        public List<(string ToolCallId, AgentSpeechMessage Msg)> Buffered { get; } = [];
        public Task EmitTurnStartAsync(string s, string n, string t, CancellationToken ct)
        { Events.Add($"start:{s}->{t}"); return Task.CompletedTask; }
        public Task EmitTurnDeltaAsync(string d, CancellationToken ct)
        { Events.Add($"delta:{d}"); return Task.CompletedTask; }
        public Task EmitTurnEndAsync(string text, CancellationToken ct)
        { Events.Add($"end:{text}"); return Task.CompletedTask; }
        public void BufferForInitiator(string toolCallId, AgentSpeechMessage m)
            => Buffered.Add((toolCallId, m));
    }

    private sealed class ReplyOnce(string reply) : IModelProvider
    {
        public string Id => "stub";
        public string Name => "Stub";
        public string EnvironmentKey => "STUB";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default)
            => CompletionEventStream.Create(stream =>
            {
                var msg = new CompletionAssistantMessage
                {
                    Content = [new CompletionTextContent { Text = reply }],
                    Model = model.Id,
                    CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                    CompletionStopReason = CompletionStopReason.Stop,
                };
                stream.Push(new CompletionTextDeltaEvent { ContentIndex = 0, Delta = reply, Partial = msg });
                stream.Push(new CompletionDoneEvent { Reason = CompletionStopReason.Stop, CompletionMessage = msg });
                stream.End();
                return Task.CompletedTask;
            });
    }
}
