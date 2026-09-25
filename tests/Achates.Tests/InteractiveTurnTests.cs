using System.Text.Json;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Server;
using Achates.Server.Mobile;
using Achates.Server.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achates.Tests;

public sealed class InteractiveTurnTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _ticks, duration.Ticks);
            ManualTimer[] timers;
            lock (_timers) timers = [.. _timers];
            foreach (var timer in timers) timer.Tick();
        }
        private sealed class ManualTimer(Action tick) : ITimer
        {
            private bool _disposed;
            public void Tick() { if (!_disposed) tick(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public void Productive_work_can_exceed_old_limit_but_silence_times_out_once()
    {
        var time = new ManualTime();
        var reasons = new List<string>();
        using var watchdog = new InteractiveTurnWatchdog(null, reasons.Add, time);
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            watchdog.Progress();
        }
        Assert.Empty(reasons);
        time.Advance(TimeSpan.FromMinutes(5));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(["idle_timeout"], reasons);
    }

    [Fact]
    public void Configured_overall_limit_still_caps_continuous_output_and_stop_disarms_timer()
    {
        var time = new ManualTime();
        using var watchdog = new InteractiveTurnWatchdog(
            new InteractiveConfig { MaxDurationSeconds = 120, IdleTimeoutSeconds = 90 }, _ => { }, time);
        time.Advance(TimeSpan.FromMinutes(1));
        watchdog.Progress();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("duration_limit", watchdog.TimeoutReason);
        using var completed = new InteractiveTurnWatchdog(null, _ => Assert.Fail("Completed turn timed out"), time);
        completed.Stop();
        time.Advance(TimeSpan.FromHours(2));
    }

    private static CompletionAssistantMessage Message(Model model, params CompletionContent[] content) => new()
    {
        Content = content, Model = model.Id, CompletionUsage = CompletionUsage.Empty,
        CompletionStopReason = CompletionStopReason.Stop,
    };

    private static Model Model(IModelProvider provider, string id = "base") => new()
    {
        Id = id, Name = id, Provider = provider, Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000, Input = ModelModalities.Text | ModelModalities.File,
        Output = ModelModalities.Text, Parameters = ModelParameters.Tools,
    };

    [Fact]
    public async Task Nested_content_counts_as_progress_but_empty_lifecycle_events_do_not()
    {
        var time = new ManualTime();
        using var watchdog = new InteractiveTurnWatchdog(null, _ => { }, time);
        var model = Model(new ScenarioProvider(time));
        var msg = Message(model);
        using (CompletionActivity.Observe(watchdog.Progress))
        {
            await Task.Run(() =>
            {
                var stream = new CompletionEventStream();
                for (var i = 0; i < 15; i++)
                {
                    time.Advance(TimeSpan.FromMinutes(1));
                    stream.Push(new CompletionThinkingDeltaEvent { ContentIndex = 0, Delta = "work", Partial = msg });
                }
                Assert.Null(watchdog.TimeoutReason);
                for (var i = 0; i < 5; i++)
                {
                    stream.Push(new CompletionStartEvent { Partial = msg });
                    stream.Push(new CompletionTextDeltaEvent { ContentIndex = 0, Delta = "", Partial = msg });
                    time.Advance(TimeSpan.FromMinutes(1));
                }
            });
        }
        Assert.Equal("idle_timeout", watchdog.TimeoutReason);
    }

    private sealed class ScenarioProvider(ManualTime time) : IModelProvider
    {
        public string Id => "scenario";
        public string Name => Id;
        public string EnvironmentKey => "UNUSED";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }
        public int ThinkCalls;
        public int BaseCalls;
        public TaskCompletionSource ReadyToTimeout = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CompletionContext? ResumedContext;
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public Task<CompletionImageContent> GenerateImageAsync(Model m, string prompt, CancellationToken ct = default)
            => throw new NotSupportedException();
        public CompletionEventStream GetCompletions(Model model, CompletionContext context,
            CompletionOptions? options = null, CancellationToken ct = default) => CompletionEventStream.Create(async stream =>
        {
            CompletionAssistantMessage msg;
            if (model.Id == "think")
            {
                ThinkCalls++;
                msg = Message(model, new CompletionTextContent { Text = "The completed analysis" });
                // A nested ThinkTool call can legitimately exceed the former 12-minute cap.
                for (var i = 0; i < 15; i++)
                {
                    stream.Push(new CompletionThinkingDeltaEvent { ContentIndex = 0, Delta = "work", Partial = msg });
                    time.Advance(TimeSpan.FromMinutes(1));
                }
            }
            else if (++BaseCalls == 1)
            {
                msg = Message(model, new CompletionToolCall
                {
                    Id = "think-1", Name = "think", Arguments = new() { ["prompt"] = "Analyze these documents" },
                }) with { CompletionStopReason = CompletionStopReason.ToolUse };
            }
            else if (BaseCalls == 2)
            {
                ReadyToTimeout.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }
            else
            {
                ResumedContext = context;
                msg = Message(model, new CompletionTextContent { Text = "Here is the final answer" });
            }
            stream.Push(new CompletionDoneEvent { Reason = msg.CompletionStopReason, CompletionMessage = msg });
            stream.End();
        });
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type type) => null;
    }

    private sealed class TestSocket : WebSocket
    {
        private readonly Channel<byte[]> _requests = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<JsonElement> _sent = Channel.CreateUnbounded<JsonElement>();
        private bool _closed;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _closed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => _closed = true;
        public override void Dispose() => _closed = true;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
            { _closed = true; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
            => CloseAsync(status, description, ct);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var bytes = await _requests.Reader.ReadAsync(ct);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory());
            _sent.Writer.TryWrite(doc.RootElement.Clone());
            return Task.CompletedTask;
        }
        public void Request(string method, string id, object? parameters = null) => _requests.Writer.TryWrite(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "req", id, method, @params = parameters ?? new { agent = "vivian", session_id = "session" } })));
        public async Task<JsonElement> ReadUntil(Func<JsonElement, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var frame = await _sent.Reader.ReadAsync(timeout.Token);
                if (predicate(frame)) return frame;
            }
        }
        public Task<JsonElement> Done() => ReadUntil(f => f.TryGetProperty("event", out var e) && e.GetString() == "done");
        public Task<JsonElement> Response(string id) => ReadUntil(f => f.TryGetProperty("id", out var i) && i.GetString() == id);
    }

    [Fact]
    public async Task Timeout_saves_notice_and_completed_thinking_then_resume_after_reload_keeps_them()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var time = new ManualTime();
            var provider = new ScenarioProvider(time);
            var model = Model(provider);
            var think = new ThinkTool(Model(provider, "think"), "vivian");
            var store = new MobileSessionStore(directory.FullName);
            var definition = new AgentDefinition
            {
                Model = model, SystemPrompt = "Test", Tools = [think], CompletionOptions = null,
                MemoryPath = Path.Combine(directory.FullName, "memory.md"),
                WorkingMemoryPath = Path.Combine(directory.FullName, "working.md"),
            };
            var transport = new MobileTransport(new Dictionary<string, AgentDefinition> { ["vivian"] = definition }, store,
                new ReadStateStore(directory.FullName), new AgentStateCache(), NullLoggerFactory.Instance,
                new EmptyServices()) { TurnTimeProvider = time };
            await store.SaveAsync("vivian", new MobileSession { Id = "session", Title = "Test" });
            using var socket = new TestSocket();
            using var connectionCts = new CancellationTokenSource();
            var connection = transport.HandleConnectionAsync(socket, connectionCts.Token);
            var runtime = new AgentRuntime(new AgentOptions { Model = model, Tools = [think] });
            var prompt = new UserMessage
            {
                Text = "Review these documents",
                Content = [new CompletionFileContent { FileName = "statement.pdf", MimeType = "application/pdf", Data = "AA==" }],
            };
            var turn = transport.StreamAgentResponseAsync(runtime, "vivian", "session", prompt, CancellationToken.None);
            await provider.ReadyToTimeout.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromMinutes(6));
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
            var done = (await socket.Done()).GetProperty("payload");
            Assert.Equal("timed_out", done.GetProperty("status").GetString());
            Assert.True(done.GetProperty("can_continue").GetBoolean());
            socket.Request("sessions.get", "history");
            var history = (await socket.Response("history")).GetProperty("payload");
            Assert.Contains("timed out", history.GetProperty("interruption").GetString());
            Assert.True(history.GetProperty("can_continue").GetBoolean());
            var saved = (await store.LoadAsync("vivian", "session"))!;
            Assert.Contains("timed out", saved.Interruption);
            Assert.True(MobileTransport.CanContinue(saved));
            Assert.Equal(1, provider.ThinkCalls);
            Assert.Equal("The completed analysis", Assert.IsType<CompletionTextContent>(
                Assert.Single(saved.Messages.OfType<ToolResultMessage>()).Content.Single()).Text);

            // The first runtime was not cached: the RPC must reconstruct it from disk.
            socket.Request("chat.continue", "continue");
            Assert.True((await socket.Response("continue")).GetProperty("ok").GetBoolean());
            var resumed = (await socket.Done()).GetProperty("payload");
            Assert.Equal("completed", resumed.GetProperty("status").GetString());
            Assert.False(resumed.GetProperty("can_continue").GetBoolean());
            saved = (await store.LoadAsync("vivian", "session"))!;
            connectionCts.Cancel();
            await connection;
            Assert.Null(saved.Interruption);
            Assert.False(MobileTransport.CanContinue(saved));
            Assert.Single(saved.Messages.OfType<UserMessage>());
            Assert.Single(saved.Messages.OfType<ToolResultMessage>());
            Assert.Equal(1, provider.ThinkCalls);
            Assert.NotNull(provider.ResumedContext);
            Assert.Contains(provider.ResumedContext.Messages, m => m is CompletionToolResultMessage);
            var original = Assert.Single(saved.Messages.OfType<UserMessage>());
            Assert.Equal("statement.pdf", Assert.IsType<CompletionFileContent>(Assert.Single(original.Content!)).FileName);
        }
        finally { directory.Delete(true); }
    }

    private sealed class WorkbookProvider(string workbookId) : IModelProvider
    {
        public string Id => "workbook-test";
        public string EnvironmentKey => "UNUSED";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }
        public List<CompletionContext> Contexts { get; } = [];
        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);
        public CompletionEventStream GetCompletions(Model model, CompletionContext context,
            CompletionOptions? options = null, CancellationToken ct = default) => CompletionEventStream.Create(stream =>
        {
            Contexts.Add(context);
            var message = context.Messages.Last() is CompletionToolResultMessage
                ? Message(model, new CompletionTextContent { Text = "Read the saved workbook value." })
                : Message(model, new CompletionToolCall
                {
                    Id = Guid.NewGuid().ToString("N"), Name = "workbook",
                    Arguments = new() { ["action"] = "read", ["workbook_id"] = workbookId, ["sheet"] = "Sales", ["range"] = "C2" },
                }) with { CompletionStopReason = CompletionStopReason.ToolUse };
            stream.Push(new CompletionDoneEvent { Reason = message.CompletionStopReason, CompletionMessage = message });
            stream.End();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Workbook_upload_and_resubmit_work_with_text_only_model_and_restore_tool_after_restart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var bytes = WorkbookTests.CreateWorkbook();
            var attachment = WorkbookTests.Attachment(bytes);
            var provider = new WorkbookProvider(attachment.WorkbookId);
            var model = Model(provider) with { Input = ModelModalities.Text };
            var store = new MobileSessionStore(directory.FullName);
            var definition = new AgentDefinition
            {
                Model = model, SystemPrompt = "Test", Tools = [], CompletionOptions = null,
                MemoryPath = Path.Combine(directory.FullName, "memory.md"),
                WorkingMemoryPath = Path.Combine(directory.FullName, "working.md"),
            };
            await store.SaveAsync("vivian", new MobileSession { Id = "session", Title = "Workbook test" });
            for (var run = 0; run < 2; run++)
            {
                var transport = new MobileTransport(new Dictionary<string, AgentDefinition> { ["vivian"] = definition }, store,
                    new ReadStateStore(directory.FullName), new AgentStateCache(), NullLoggerFactory.Instance, new EmptyServices());
                using var socket = new TestSocket();
                using var connectionCts = new CancellationTokenSource();
                var connection = transport.HandleConnectionAsync(socket, connectionCts.Token);
                try
                {
                    if (run == 0)
                        socket.Request("chat.send", "send", new { agent = "vivian", session_id = "session", text = "Read C2",
                            attachments = new[] { new { mime = CompletionWorkbookContent.ExcelMime, data = attachment.Data, filename = attachment.FileName } } });
                    else
                        socket.Request("chat.resubmit", "send"); // preserve original upload after restart
                    Assert.True((await socket.Response("send")).GetProperty("ok").GetBoolean());
                    Assert.Equal("completed", (await socket.Done()).GetProperty("payload").GetProperty("status").GetString());
                    var saved = (await store.LoadAsync("vivian", "session"))!;
                    var original = Assert.Single(saved.Messages.OfType<UserMessage>());
                    Assert.Equal(attachment.Data, Assert.IsType<CompletionWorkbookContent>(Assert.Single(original.Content!)).Data);
                    var toolResult = Assert.Single(saved.Messages.OfType<ToolResultMessage>());
                    Assert.Equal("workbook", toolResult.ToolName);
                    Assert.Contains("99", Assert.IsType<CompletionTextContent>(Assert.Single(toolResult.Content)).Text);
                    socket.Request("sessions.get", "history");
                    var history = (await socket.Response("history")).GetProperty("payload").GetProperty("messages");
                    var historyUser = history.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user");
                    Assert.Equal(attachment.Data, historyUser.GetProperty("content")[0].GetProperty("data").GetString());
                }
                finally { connectionCts.Cancel(); await connection; }
            }
            Assert.All(provider.Contexts, context =>
            {
                Assert.Contains(context.Tools!, tool => tool.Name == "workbook");
                Assert.DoesNotContain(context.Messages.OfType<CompletionUserContentMessage>().SelectMany(m => m.Content),
                    block => block is CompletionWorkbookContent or CompletionFileContent);
            });
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Interrupted_tool_batch_keeps_completed_results_and_does_not_replay_missing_calls()
    {
        List<AgentMessage> messages =
        [
            new UserMessage { Text = "Do work" },
            new AssistantMessage
            {
                Model = "base", StopReason = CompletionStopReason.ToolUse, Usage = CompletionUsage.Empty,
                Content = [new CompletionToolCall { Id = "one", Name = "write", Arguments = [] },
                           new CompletionToolCall { Id = "two", Name = "write", Arguments = [] }],
            },
            new ToolResultMessage { ToolCallId = "one", ToolName = "write", Content = [new CompletionTextContent { Text = "Saved" }] },
        ];
        AgentLoop.CloseInterruptedToolCalls(messages);
        AgentLoop.CloseInterruptedToolCalls(messages);
        Assert.Equal(4, messages.Count);
        var missing = Assert.IsType<ToolResultMessage>(messages.Last());
        Assert.Equal("two", missing.ToolCallId);
        Assert.True(missing.IsError);
        Assert.Contains("outcome is unknown", Assert.IsType<CompletionTextContent>(missing.Content.Single()).Text);
    }
}
