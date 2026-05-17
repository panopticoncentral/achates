using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Server.Mobile;
using Achates.Server.Tools;

namespace Achates.Tests;

/// <summary>
/// Locks the runtime-flow contract that <c>MobileTransport.StreamAgentResponseAsync</c>
/// relies on: a sink set in <see cref="ChatSinkAccessor.Current"/> BEFORE
/// <see cref="AgentRuntime.PromptAsync"/> must be observable from inside a tool the
/// agent loop executes. PromptAsync eagerly Task.Run's the loop and snapshots the
/// ambient ExecutionContext at that moment, so an AsyncLocal set afterward would
/// never flow in — which is exactly the bug this test guards against.
/// </summary>
public sealed class ChatSinkFlowTests
{
    /// <summary>Record-only fake sink; presence is all the probe checks.</summary>
    private sealed class FlowProbeSink : IChatSink
    {
        public Task EmitTurnStartAsync(string s, string n, string t, CancellationToken ct) => Task.CompletedTask;
        public Task EmitTurnDeltaAsync(string d, CancellationToken ct) => Task.CompletedTask;
        public Task EmitTurnEndAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public void BufferForInitiator(string toolCallId, AgentSpeechMessage m) { }
    }

    /// <summary>
    /// First completion: an assistant message that calls the <c>probe</c> tool
    /// (StopReason ToolUse). Second completion (after the tool result): plain
    /// "done" text with StopReason Stop. Mirrors the streaming/Done pattern in
    /// <c>ChatRoomManagerTests.ReplyProvider</c>.
    /// </summary>
    private sealed class ProbeCallProvider : IModelProvider
    {
        private int _calls;
        public string Id => "flow-stub";
        public string Name => "FlowStub";
        public string EnvironmentKey => "FLOW_STUB";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }

        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);

        public CompletionEventStream GetCompletions(
            Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default)
            => CompletionEventStream.Create(stream =>
            {
                var call = Interlocked.Increment(ref _calls);
                if (call == 1)
                {
                    var toolMsg = new CompletionAssistantMessage
                    {
                        Content =
                        [
                            new CompletionToolCall
                            {
                                Id = "probe-1",
                                Name = "probe",
                                Arguments = [],
                            },
                        ],
                        Model = model.Id,
                        CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                        CompletionStopReason = CompletionStopReason.ToolUse,
                    };
                    stream.Push(new CompletionDoneEvent
                    {
                        Reason = CompletionStopReason.ToolUse,
                        CompletionMessage = toolMsg,
                    });
                }
                else
                {
                    var doneMsg = new CompletionAssistantMessage
                    {
                        Content = [new CompletionTextContent { Text = "done" }],
                        Model = model.Id,
                        CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                        CompletionStopReason = CompletionStopReason.Stop,
                    };
                    stream.Push(new CompletionTextDeltaEvent { ContentIndex = 0, Delta = "done", Partial = doneMsg });
                    stream.Push(new CompletionDoneEvent
                    {
                        Reason = CompletionStopReason.Stop,
                        CompletionMessage = doneMsg,
                    });
                }
                stream.End();
                return Task.CompletedTask;
            });
    }

    /// <summary>Test-only tool that records whether the chat sink flowed into the loop.</summary>
    private sealed class ProbeTool(Action<IChatSink?> capture) : AgentTool
    {
        public override string Name => "probe";
        public override string Description => "Records the ambient chat sink.";
        public override JsonElement Parameters =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            Func<AgentToolResult, Task>? onProgress = null)
        {
            capture(ChatSinkAccessor.Current);
            return Task.FromResult(new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "ok" }],
            });
        }
    }

    private static Model FlowModel(IModelProvider provider) => new()
    {
        Id = "test/flow-model",
        Name = "FlowTest",
        Provider = provider,
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000,
        Input = ModelModalities.Text,
        Output = ModelModalities.Text,
        Parameters = ModelParameters.Tools,
    };

    [Fact]
    public async Task Sink_set_before_PromptAsync_flows_into_tool_executed_by_agent_loop()
    {
        IChatSink? observed = null;
        var probeRan = false;

        try
        {
            var runtime = new AgentRuntime(new AgentOptions
            {
                Model = FlowModel(new ProbeCallProvider()),
                SystemPrompt = "test",
                Tools = [new ProbeTool(sink => { probeRan = true; observed = sink; })],
            });

            // Bind the sink BEFORE PromptAsync. PromptAsync eagerly Task.Run's the
            // agent loop and snapshots ExecutionContext here; the old (buggy)
            // ordering set this AFTER and the sink never flowed into the tool.
            var expected = new FlowProbeSink();
            ChatSinkAccessor.Current = expected;

            var stream = runtime.PromptAsync("go");

            await foreach (var _ in stream)
            {
                // drain to completion
            }

            Assert.True(probeRan, "probe tool was never executed by the agent loop");
            Assert.NotNull(observed);
            Assert.Same(expected, observed);
        }
        finally
        {
            ChatSinkAccessor.Current = null;
        }
    }
}
