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
using Microsoft.Extensions.Logging.Abstractions;

namespace Achates.Tests;

/// <summary>
/// Pins that a turn's persistence is INDEPENDENT of the initiating WebSocket
/// connection. A client that drops mid-turn (backgrounding, navigation, a network
/// blip) cancels the connection token; the agent runtime nonetheless runs to
/// completion on its own token. The fully-generated reply must still reach disk —
/// the regression where it didn't is what made nudges + replies "disappear" on reopen.
/// </summary>
public sealed class MobileTransportPersistenceTests
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Streams one text delta then completes with Stop — a single, no-tool turn.</summary>
    private sealed class SingleReplyProvider : IModelProvider
    {
        public string Id => "persist-stub";
        public string Name => "PersistStub";
        public string EnvironmentKey => "PERSIST_STUB";
        public string? Key { get; set; }
        public HttpClient? HttpClient { get; set; }

        public Task<IReadOnlyList<Model>> GetModelsAsync(ModelModalities? o = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Model>>([]);

        public Task<CompletionImageContent> GenerateImageAsync(
            Model model, string prompt, CancellationToken ct = default)
            => throw new NotSupportedException();

        public CompletionEventStream GetCompletions(
            Model model, CompletionContext context, CompletionOptions? options = null, CancellationToken ct = default)
            => CompletionEventStream.Create(stream =>
            {
                var msg = new CompletionAssistantMessage
                {
                    Content = [new CompletionTextContent { Text = "hello there" }],
                    Model = model.Id,
                    CompletionUsage = new CompletionUsage { Cost = new CompletionUsageCost() },
                    CompletionStopReason = CompletionStopReason.Stop,
                };
                stream.Push(new CompletionTextDeltaEvent { ContentIndex = 0, Delta = "hello there", Partial = msg });
                stream.Push(new CompletionDoneEvent { Reason = CompletionStopReason.Stop, CompletionMessage = msg });
                stream.End();
                return Task.CompletedTask;
            });
    }

    private static Model TestModel(IModelProvider provider) => new()
    {
        Id = "test/persist-model",
        Name = "PersistTest",
        Provider = provider,
        Cost = new ModelCost { Prompt = 0, Completion = 0 },
        ContextWindow = 128_000,
        Input = ModelModalities.Text,
        Output = ModelModalities.Text,
        Parameters = ModelParameters.Tools,
    };

    [Fact]
    public async Task Completed_turn_is_persisted_even_when_connection_token_is_cancelled()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var store = new MobileSessionStore(tmp.FullName);
            var transport = new MobileTransport(
                new Dictionary<string, AgentDefinition>(), // empty: skips cost/speech/title paths
                store,
                new AgentStateCache(),
                NullLoggerFactory.Instance,
                new NullServiceProvider());

            var runtime = new AgentRuntime(new AgentOptions
            {
                Model = TestModel(new SingleReplyProvider()),
                SystemPrompt = "test",
                Tools = [],
            });

            var userMessage = new UserMessage { Text = "nudge" };

            // Simulate the initiating client having already dropped: its connection
            // token is cancelled. The turn must still persist.
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await transport.StreamAgentResponseAsync(runtime, "vivian", "sess-1", userMessage, cts.Token);

            var loaded = await store.LoadAsync("vivian", "sess-1");
            Assert.NotNull(loaded);
            Assert.Contains(loaded!.Messages, m => m is UserMessage u && u.Text == "nudge");
            var assistant = Assert.IsType<AssistantMessage>(
                loaded.Messages.LastOrDefault(m => m is AssistantMessage));
            Assert.Contains(assistant.Content.OfType<CompletionTextContent>(), c => c.Text.Contains("hello there"));
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
