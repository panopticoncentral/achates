using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Tests;

public sealed class MessageConversionTests
{
    // --- UserMessage ---

    [Fact]
    public void UserMessage_without_content_becomes_text_message()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "Hello", Timestamp = 1000 },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        var msg = Assert.Single(result);
        var textMsg = Assert.IsType<CompletionUserTextMessage>(msg);
        Assert.Equal("Hello", textMsg.Text);
        Assert.Equal(1000, textMsg.Timestamp);
    }

    [Fact]
    public void UserMessage_with_content_becomes_content_message()
    {
        var imageContent = new CompletionImageContent { Data = "base64data", MimeType = "image/png" };
        var messages = new AgentMessage[]
        {
            new UserMessage
            {
                Text = "Look at this",
                Content = [imageContent],
                Timestamp = 2000,
            },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        var msg = Assert.Single(result);
        var contentMsg = Assert.IsType<CompletionUserContentMessage>(msg);

        // Text is prepended as the first content block
        Assert.Equal(2, contentMsg.Content.Count);
        var textBlock = Assert.IsType<CompletionTextContent>(contentMsg.Content[0]);
        Assert.Equal("Look at this", textBlock.Text);
        Assert.IsType<CompletionImageContent>(contentMsg.Content[1]);
    }

    // --- AssistantMessage ---

    [Fact]
    public void AssistantMessage_maps_all_fields()
    {
        var messages = new AgentMessage[]
        {
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "Hi" }],
                Model = "test-model",
                Usage = new CompletionUsage { Input = 10, Output = 5, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
                Error = "some error",
                Timestamp = 3000,
            },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        var msg = Assert.Single(result);
        var assistantMsg = Assert.IsType<CompletionAssistantMessage>(msg);
        Assert.Equal("test-model", assistantMsg.Model);
        Assert.Equal(10, assistantMsg.CompletionUsage.Input);
        Assert.Equal(CompletionStopReason.Stop, assistantMsg.CompletionStopReason);
        Assert.Equal("some error", assistantMsg.ErrorMessage);
        Assert.Equal(3000, assistantMsg.Timestamp);
    }

    // --- ToolResultMessage ---

    [Fact]
    public void ToolResultMessage_maps_all_fields()
    {
        var messages = new AgentMessage[]
        {
            new ToolResultMessage
            {
                ToolCallId = "call_1",
                ToolName = "session",
                Content = [new CompletionTextContent { Text = "result" }],
                IsError = true,
                Details = "extra info",
                Timestamp = 4000,
            },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        var msg = Assert.Single(result);
        var toolMsg = Assert.IsType<CompletionToolResultMessage>(msg);
        Assert.Equal("call_1", toolMsg.ToolCallId);
        Assert.Equal("session", toolMsg.ToolName);
        Assert.True(toolMsg.IsError);
        Assert.Equal("extra info", toolMsg.Details);
        Assert.Equal(4000, toolMsg.Timestamp);
    }

    // --- SummaryMessage ---

    [Fact]
    public void SummaryMessage_becomes_user_text_with_prefix()
    {
        var messages = new AgentMessage[]
        {
            new SummaryMessage { Summary = "Earlier we discussed X.", Timestamp = 5000 },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        var msg = Assert.Single(result);
        var textMsg = Assert.IsType<CompletionUserTextMessage>(msg);
        Assert.Contains("[Summary of earlier conversation]", textMsg.Text);
        Assert.Contains("Earlier we discussed X.", textMsg.Text);
    }

    // --- Mixed conversation ---

    [Fact]
    public void Full_conversation_preserves_order_and_types()
    {
        var messages = new AgentMessage[]
        {
            new UserMessage { Text = "Hi" },
            new AssistantMessage
            {
                Content = [new CompletionTextContent { Text = "Hello!" }],
                Model = "m",
                Usage = new CompletionUsage { Input = 1, Output = 1, Cost = new CompletionUsageCost() },
                StopReason = CompletionStopReason.Stop,
            },
            new ToolResultMessage
            {
                ToolCallId = "c1",
                ToolName = "t",
                Content = [new CompletionTextContent { Text = "r" }],
            },
            new SummaryMessage { Summary = "S" },
        };

        var result = MessageConversion.DefaultConvertToLlm(messages);

        Assert.Equal(4, result.Count);
        Assert.IsType<CompletionUserTextMessage>(result[0]);
        Assert.IsType<CompletionAssistantMessage>(result[1]);
        Assert.IsType<CompletionToolResultMessage>(result[2]);
        Assert.IsType<CompletionUserTextMessage>(result[3]); // summary
    }

    // --- Edge cases ---

    [Fact]
    public void Empty_list_returns_empty()
    {
        var result = MessageConversion.DefaultConvertToLlm([]);

        Assert.Empty(result);
    }
}
