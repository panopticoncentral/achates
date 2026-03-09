using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Server;

namespace Achates.Tests;

public sealed class FileSessionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"achates-test-{Guid.NewGuid()}");
    private readonly FileSessionStore _store;

    public FileSessionStoreTests()
    {
        _store = new FileSessionStore(_basePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_returns_null_for_nonexistent_session()
    {
        var result = await _store.LoadAsync("no-such-session");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip_user_message()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "Hello", Timestamp = 1000 },
        };

        await _store.SaveAsync("ch:peer", messages);
        var loaded = await _store.LoadAsync("ch:peer");

        Assert.NotNull(loaded);
        var msg = Assert.Single(loaded);
        var user = Assert.IsType<UserMessage>(msg);
        Assert.Equal("Hello", user.Text);
        Assert.Equal(1000, user.Timestamp);
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip_assistant_message()
    {
        var messages = new AgentMessage[]
        {
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "Hi there" }],
                Model = "test-model",
                Usage = new CompletionUsage { Input = 10, Output = 5, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
                Timestamp = 2000,
            },
        };

        await _store.SaveAsync("ch:peer", messages);
        var loaded = await _store.LoadAsync("ch:peer");

        Assert.NotNull(loaded);
        var assistant = Assert.IsType<AssistantMessage>(Assert.Single(loaded));
        Assert.Equal("test-model", assistant.Model);
        Assert.Equal(CompletionStopReason.Stop, assistant.StopReason);
        Assert.Equal(10, assistant.Usage.Input);
        Assert.Equal(5, assistant.Usage.Output);

        var text = Assert.IsType<CompletionTextContent>(Assert.Single(assistant.Content));
        Assert.Equal("Hi there", text.Text);
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip_tool_result_message()
    {
        var messages = new AgentMessage[]
        {
            new ToolResultMessage
            {
                ToolCallId = "call_123",
                ToolName = "session",
                Content = [new CompletionTextContent { Text = "result data" }],
                IsError = false,
                Timestamp = 3000,
            },
        };

        await _store.SaveAsync("ch:peer", messages);
        var loaded = await _store.LoadAsync("ch:peer");

        Assert.NotNull(loaded);
        var toolResult = Assert.IsType<ToolResultMessage>(Assert.Single(loaded));
        Assert.Equal("call_123", toolResult.ToolCallId);
        Assert.Equal("session", toolResult.ToolName);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip_full_conversation()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "What time is it?", Timestamp = 1000 },
            new AssistantMessage
            {
                Content =
                [
                    new CompletionThinkingContent { Thinking = "I should use the session tool" },
                    new CompletionToolCall
                    {
                        Id = "call_1",
                        Name = "session",
                        Arguments = new Dictionary<string, object?> { ["action"] = "info" },
                    },
                ],
                Model = "claude-sonnet",
                Usage = new CompletionUsage { Input = 50, Output = 20, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.ToolUse,
                Timestamp = 2000,
            },
            new ToolResultMessage
            {
                ToolCallId = "call_1",
                ToolName = "session",
                Content = [new CompletionTextContent { Text = "Session started at 10:00" }],
                Timestamp = 3000,
            },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "The session started at 10:00." }],
                Model = "claude-sonnet",
                Usage = new CompletionUsage { Input = 80, Output = 15, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
                Timestamp = 4000,
            },
        };

        await _store.SaveAsync("convo:peer1", messages);
        var loaded = await _store.LoadAsync("convo:peer1");

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Count);
        Assert.IsType<UserMessage>(loaded[0]);
        Assert.IsType<AssistantMessage>(loaded[1]);
        Assert.IsType<ToolResultMessage>(loaded[2]);
        Assert.IsType<AssistantMessage>(loaded[3]);

        // Verify the thinking content survived
        var firstAssistant = (AssistantMessage)loaded[1];
        Assert.Equal(2, firstAssistant.Content.Count);
        Assert.IsType<CompletionThinkingContent>(firstAssistant.Content[0]);
        Assert.IsType<CompletionToolCall>(firstAssistant.Content[1]);

        var toolCall = (CompletionToolCall)firstAssistant.Content[1];
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("session", toolCall.Name);
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_session()
    {
        await _store.SaveAsync("ch:peer", [new UserMessage { Text = "first" }]);
        await _store.SaveAsync("ch:peer", [new UserMessage { Text = "second" }]);

        var loaded = await _store.LoadAsync("ch:peer");

        Assert.NotNull(loaded);
        var msg = Assert.IsType<UserMessage>(Assert.Single(loaded));
        Assert.Equal("second", msg.Text);
    }

    [Fact]
    public async Task DeleteAsync_removes_session()
    {
        await _store.SaveAsync("ch:peer", [new UserMessage { Text = "hello" }]);
        await _store.DeleteAsync("ch:peer");

        var loaded = await _store.LoadAsync("ch:peer");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_for_nonexistent_session()
    {
        await _store.DeleteAsync("no-such:key");
        // Should not throw
    }

    [Fact]
    public async Task Sessions_are_isolated_by_key()
    {
        await _store.SaveAsync("ch:a", [new UserMessage { Text = "alpha" }]);
        await _store.SaveAsync("ch:b", [new UserMessage { Text = "beta" }]);

        var a = await _store.LoadAsync("ch:a");
        var b = await _store.LoadAsync("ch:b");

        Assert.Equal("alpha", Assert.IsType<UserMessage>(Assert.Single(a!)).Text);
        Assert.Equal("beta", Assert.IsType<UserMessage>(Assert.Single(b!)).Text);
    }

    [Fact]
    public async Task Session_key_maps_to_channel_peer_directory_structure()
    {
        await _store.SaveAsync("telegram:12345", [new UserMessage { Text = "hi" }]);

        var expectedPath = Path.Combine(_basePath, "telegram", "12345.json");
        Assert.True(File.Exists(expectedPath));
    }
}
