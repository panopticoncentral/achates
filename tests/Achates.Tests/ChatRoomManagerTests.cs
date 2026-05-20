using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Server.Chat;
using Achates.Server.Mobile;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class ChatRoomManagerTests
{
    private sealed class FakeSink : IChatSink
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

    private sealed class ReplyProvider(string reply) : IModelProvider
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

    private sealed class ThrowingProvider : IModelProvider
    {
        public string Id => "stub"; public string Name => "Stub"; public string EnvironmentKey => "S";
        public string? Key { get; set; } public HttpClient? HttpClient { get; set; }
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model m, CompletionContext c, CompletionOptions? o = null, CancellationToken ct = default)
            => throw new InvalidOperationException("network down");
    }

    private static Model ModelWith(IModelProvider provider) => new()
    {
        Id = "test/model", Name = "Test", Provider = provider,
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000, Input = ModelModalities.Text,
        Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
    };

    [Fact]
    public async Task Ask_persists_attributed_messages_to_one_continuing_target_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(dir);
            var mgr = new ChatRoomManager(store,
                _ => new AgentRuntimeFactory(ModelWith(new ReplyProvider("Sure, here's my take."))));
            var sink = new FakeSink();

            var reply1 = await mgr.AskAsync("val", "sess-1", "claire", "What do you think?", "tc-1", sink, default);
            Assert.Equal("Sure, here's my take.", reply1);
            await mgr.AskAsync("val", "sess-1", "claire", "And the risks?", "tc-2", sink, default);

            var id = MobileSessionStore.ChatSessionId("sess-1", "claire");
            var session = await store.LoadAsync("claire", id);
            Assert.NotNull(session);
            Assert.Equal(SessionSource.Chat, session!.Source);
            var speeches = session.Messages.OfType<AgentSpeechMessage>().ToList();
            Assert.Equal(4, speeches.Count);
            Assert.Equal("val", speeches[0].SpeakerAgentId);
            Assert.Equal("claire", speeches[1].SpeakerAgentId);

            var (sessions, _) = await store.ListAsync("claire");
            Assert.Single(sessions);

            Assert.Contains("start:val->claire", sink.Events);
            Assert.Contains("delta:Sure, here's my take.", sink.Events);
            Assert.Equal(4, sink.Buffered.Count);
            Assert.Equal("tc-1", sink.Buffered[0].ToolCallId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ask_seeds_target_runtime_from_prior_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(dir);
            var mgr = new ChatRoomManager(store,
                _ => new AgentRuntimeFactory(ModelWith(new ReplyProvider("ack"))));
            var sink = new FakeSink();
            await mgr.AskAsync("val", "s", "claire", "round one", "t1", sink, default);
            await mgr.AskAsync("val", "s", "claire", "round two", "t2", sink, default);

            var id = MobileSessionStore.ChatSessionId("s", "claire");
            var session = await store.LoadAsync("claire", id);
            var texts = session!.Messages.OfType<AgentSpeechMessage>().Select(m => m.Text).ToList();
            Assert.Equal(["round one", "ack", "round two", "ack"], texts);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ask_records_failure_text_when_target_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(dir);
            var mgr = new ChatRoomManager(store,
                _ => new AgentRuntimeFactory(ModelWith(new ThrowingProvider())));
            var sink = new FakeSink();
            var reply = await mgr.AskAsync("val", "s", "claire", "hi", "t1", sink, default);
            Assert.Contains("consult failed", reply);
            Assert.Contains("network down", reply);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ask_records_target_cost_to_ledger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ledgerPath = Path.Combine(dir, "claire-costs.jsonl");
            var ledger = new Achates.Server.CostLedger(ledgerPath);
            var store = new MobileSessionStore(dir);
            var mgr = new ChatRoomManager(store,
                _ => new AgentRuntimeFactory(ModelWith(new ReplyProvider("hello")), ledger: ledger));
            var sink = new FakeSink();
            await mgr.AskAsync("val", "s", "claire", "hi", "t1", sink, default);
            Assert.True(File.Exists(ledgerPath));
            var lines = await File.ReadAllTextAsync(ledgerPath);
            Assert.Contains("\"channel\":\"chat\"", lines);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ask_round_completes_when_universal_tools_provided()
    {
        var dir = Path.Combine(Path.GetTempPath(), "achates-crm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(dir);

            var mgr = new ChatRoomManager(store,
                _ =>
                {
                    var memoryTool = new MemoryTool(
                        Path.Combine(dir, "shared.md"),
                        Path.Combine(dir, "agent.md"));
                    return new AgentRuntimeFactory(
                        ModelWith(new ReplyProvider("ok")),
                        universalTools: [memoryTool]);
                });

            var sink = new FakeSink();
            await mgr.AskAsync("val", "s", "claire", "hello", "t1", sink, default);

            // Reload the chat session and verify it was produced (i.e. the round completed
            // through the wiring with universal tools attached to the target runtime).
            var id = MobileSessionStore.ChatSessionId("s", "claire");
            var session = await store.LoadAsync("claire", id);
            Assert.NotNull(session);
            Assert.Equal(2, session!.Messages.OfType<AgentSpeechMessage>().Count());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
