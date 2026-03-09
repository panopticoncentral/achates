using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Tests;

public sealed class SessionCompactorTests
{
    // --- Token estimation ---

    [Fact]
    public void EstimateTokenCount_uses_character_heuristic_when_no_assistant_messages()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = new string('x', 400) }, // ~100 tokens
        };

        var estimate = SessionCompactor.EstimateTokenCount(messages);
        Assert.Equal(100, estimate);
    }

    [Fact]
    public void EstimateTokenCount_uses_last_assistant_usage_as_baseline()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "hello" },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "hi" }],
                Model = "m",
                Usage = new CompletionUsage { Input = 500, Output = 50, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
            },
            new UserMessage { Text = new string('x', 200) }, // ~50 tokens
        };

        var estimate = SessionCompactor.EstimateTokenCount(messages);
        // 500 (input) + 50 (output) + 50 (new user message) = 600
        Assert.Equal(600, estimate);
    }

    [Fact]
    public void EstimateMessageTokens_returns_at_least_1()
    {
        var message = new UserMessage { Text = "" };
        var estimate = SessionCompactor.EstimateMessageTokens(message);
        Assert.True(estimate >= 1);
    }

    // --- Split index ---

    [Fact]
    public void FindSplitIndex_returns_0_when_nothing_to_remove()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "a" },
            new UserMessage { Text = "b" },
            new UserMessage { Text = "c" },
            new UserMessage { Text = "d" },
            new UserMessage { Text = "e" },
        };

        var splitIndex = SessionCompactor.FindSplitIndex(messages, 100, 100);
        Assert.Equal(0, splitIndex);
    }

    [Fact]
    public void FindSplitIndex_preserves_minimum_messages()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = new string('x', 400) },
            new UserMessage { Text = new string('x', 400) },
            new UserMessage { Text = new string('x', 400) },
            new UserMessage { Text = new string('x', 400) },
            new UserMessage { Text = new string('x', 400) },
        };

        // Even with huge tokens to remove, preserve at least 4 messages
        var splitIndex = SessionCompactor.FindSplitIndex(messages, 10000, 100);
        Assert.Equal(1, splitIndex); // 5 - 4 = max split at 1
    }

    [Fact]
    public void FindSplitIndex_does_not_split_mid_tool_sequence()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = new string('x', 400) },
            new AssistantMessage
            {
                Content = [new CompletionToolCall { Id = "c1", Name = "t", Arguments = [] }],
                Model = "m",
                Usage = CompletionUsage.Empty,
                StopReason = CompletionStopReason.ToolUse,
            },
            new ToolResultMessage
            {
                ToolCallId = "c1", ToolName = "t",
                Content = [new CompletionTextContent { Text = "result" }],
            },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "done" }],
                Model = "m",
                Usage = CompletionUsage.Empty,
                StopReason = CompletionStopReason.Stop,
            },
            new UserMessage { Text = "next" },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "ok" }],
                Model = "m",
                Usage = CompletionUsage.Empty,
                StopReason = CompletionStopReason.Stop,
            },
        };

        // Split should never land on a ToolResultMessage (which would orphan it from its tool call)
        var splitIndex = SessionCompactor.FindSplitIndex(messages, 10000, 5000);
        Assert.False(messages.ElementAtOrDefault(splitIndex) is ToolResultMessage,
            $"Split index {splitIndex} should not land on a ToolResultMessage");
    }

    // --- CompactIfNeededAsync ---

    [Fact]
    public async Task CompactIfNeededAsync_does_nothing_when_under_threshold()
    {
        var messages = new List<AgentMessage>
        {
            new UserMessage { Text = "hello" },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "hi" }],
                Model = "m",
                Usage = new CompletionUsage { Input = 10, Output = 5, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
            },
        };

        var model = CreateModel(contextWindow: 100_000);

        var compacted = await SessionCompactor.CompactIfNeededAsync(
            messages, model, ThrowingCompletions, CancellationToken.None);

        Assert.False(compacted);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task CompactIfNeededAsync_summarizes_when_over_threshold()
    {
        // Build a conversation that exceeds 80% of a small context window
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new UserMessage { Text = new string('x', 400) });
            messages.Add(new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = new string('y', 400) }],
                Model = "m",
                Usage = new CompletionUsage
                {
                    Input = (i + 1) * 200,
                    Output = 100,
                    Cost = new CompletionUsageCost(),
                },
                StopReason = CompletionStopReason.Stop,
            });
        }

        // Context window of 1000 — our 40 messages at ~100 tokens each = ~4000+, way over
        var model = CreateModel(contextWindow: 1000);

        var compacted = await SessionCompactor.CompactIfNeededAsync(
            messages, model, FakeSummarizationCompletions, CancellationToken.None);

        Assert.True(compacted);
        Assert.True(messages.Count < 40, "Messages should have been reduced");
        Assert.IsType<SummaryMessage>(messages[0]);
    }

    [Fact]
    public async Task CompactIfNeededAsync_falls_back_to_truncation_on_error()
    {
        var messages = new List<AgentMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new UserMessage { Text = new string('x', 400) });
            messages.Add(new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = new string('y', 400) }],
                Model = "m",
                Usage = new CompletionUsage
                {
                    Input = (i + 1) * 200,
                    Output = 100,
                    Cost = new CompletionUsageCost(),
                },
                StopReason = CompletionStopReason.Stop,
            });
        }

        var model = CreateModel(contextWindow: 1000);

        var compacted = await SessionCompactor.CompactIfNeededAsync(
            messages, model, ThrowingCompletions, CancellationToken.None);

        Assert.True(compacted);
        var summary = Assert.IsType<SummaryMessage>(messages[0]);
        Assert.Contains("truncated", summary.Summary);
    }

    // --- Helpers ---

    private static Model CreateModel(int contextWindow) =>
        new()
        {
            Id = "test-model",
            Name = "Test",
            ContextWindow = contextWindow,
            Cost = new ModelCost { Prompt = 0, Completion = 0 },
            Provider = null!,
            Input = ModelModalities.Text,
            Output = ModelModalities.Text,
            Parameters = ModelParameters.Temperature,
        };

    private static CompletionEventStream ThrowingCompletions(
        Model model, CompletionContext context, CompletionOptions? options, CancellationToken ct)
    {
        throw new InvalidOperationException("Simulated LLM failure");
    }

    private static CompletionEventStream FakeSummarizationCompletions(
        Model model, CompletionContext context, CompletionOptions? options, CancellationToken ct)
    {
        return CompletionEventStream.Create(stream =>
        {
            var result = new CompletionAssistantMessage
            {
                Content = [new CompletionTextContent { Text = "This is a summary of the conversation." }],
                Model = model.Id,
                CompletionUsage = new CompletionUsage { Input = 100, Output = 20, Cost = new CompletionUsageCost() },
                CompletionStopReason = CompletionStopReason.Stop,
            };

            stream.Push(new CompletionDoneEvent
            {
                Reason = CompletionStopReason.Stop,
                CompletionMessage = result,
            });
            stream.End();
            return Task.CompletedTask;
        });
    }
}
