using System.Text.Json;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;

namespace Achates.Tests;

/// <summary>
/// Verifies that every AgentMessage and CompletionContent subtype round-trips through
/// JSON serialization. Catches missing [JsonDerivedType] attributes.
/// </summary>
public sealed class MessageSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }

    // --- AgentMessage subtypes ---

    [Fact]
    public void UserMessage_round_trips()
    {
        AgentMessage original = new UserMessage { Text = "hello", Timestamp = 42 };
        var result = RoundTrip(original);
        var user = Assert.IsType<UserMessage>(result);
        Assert.Equal("hello", user.Text);
        Assert.Equal(42, user.Timestamp);
    }

    [Fact]
    public void AssistantMessage_round_trips()
    {
        AgentMessage original = new AssistantMessage
        {
            Content = [new CompletionTextContent { Text = "hi" }],
            Model = "m",
            Usage = new CompletionUsage { Input = 1, Output = 2, Cost = new CompletionUsageCost() },
            StopReason = CompletionStopReason.Stop,
            Error = "oops",
            Timestamp = 99,
        };
        var result = RoundTrip(original);
        var assistant = Assert.IsType<AssistantMessage>(result);
        Assert.Equal("m", assistant.Model);
        Assert.Equal("oops", assistant.Error);
        Assert.Equal(CompletionStopReason.Stop, assistant.StopReason);
    }

    [Fact]
    public void ToolResultMessage_round_trips()
    {
        AgentMessage original = new ToolResultMessage
        {
            ToolCallId = "id",
            ToolName = "name",
            Content = [new CompletionTextContent { Text = "result" }],
            IsError = true,
        };
        var result = RoundTrip(original);
        var toolResult = Assert.IsType<ToolResultMessage>(result);
        Assert.Equal("id", toolResult.ToolCallId);
        Assert.True(toolResult.IsError);
    }

    // --- CompletionContent subtypes (via AssistantMessage.Content) ---

    [Fact]
    public void CompletionTextContent_round_trips()
    {
        CompletionContent original = new CompletionTextContent { Text = "hello" };
        var result = RoundTrip(original);
        var text = Assert.IsType<CompletionTextContent>(result);
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void CompletionThinkingContent_round_trips()
    {
        CompletionContent original = new CompletionThinkingContent { Thinking = "hmm" };
        var result = RoundTrip(original);
        var thinking = Assert.IsType<CompletionThinkingContent>(result);
        Assert.Equal("hmm", thinking.Thinking);
    }

    [Fact]
    public void CompletionToolCall_round_trips()
    {
        CompletionContent original = new CompletionToolCall
        {
            Id = "call_1",
            Name = "tool",
            Arguments = new Dictionary<string, object?> { ["key"] = "value" },
        };
        var result = RoundTrip(original);
        var toolCall = Assert.IsType<CompletionToolCall>(result);
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("tool", toolCall.Name);
    }

    [Fact]
    public void CompletionImageContent_round_trips()
    {
        CompletionContent original = new CompletionImageContent { Data = "base64data", MimeType = "image/png" };
        var result = RoundTrip(original);
        var image = Assert.IsType<CompletionImageContent>(result);
        Assert.Equal("base64data", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public void CompletionAudioContent_round_trips()
    {
        CompletionContent original = new CompletionAudioContent
        {
            Id = "a1",
            Data = "audiodata",
            Format = "wav",
            Transcript = "hello",
        };
        var result = RoundTrip(original);
        var audio = Assert.IsType<CompletionAudioContent>(result);
        Assert.Equal("audiodata", audio.Data);
        Assert.Equal("wav", audio.Format);
        Assert.Equal("hello", audio.Transcript);
    }

    [Fact]
    public void CompletionAudioInputContent_round_trips()
    {
        CompletionContent original = new CompletionAudioInputContent { Data = "inputdata", Format = "mp3" };
        var result = RoundTrip(original);
        var audioInput = Assert.IsType<CompletionAudioInputContent>(result);
        Assert.Equal("inputdata", audioInput.Data);
        Assert.Equal("mp3", audioInput.Format);
    }

    [Fact]
    public void CompletionFileContent_round_trips()
    {
        CompletionContent original = new CompletionFileContent
        {
            Data = "filedata",
            MimeType = "application/pdf",
            FileName = "doc.pdf",
        };
        var result = RoundTrip(original);
        var file = Assert.IsType<CompletionFileContent>(result);
        Assert.Equal("filedata", file.Data);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal("doc.pdf", file.FileName);
    }

    [Fact]
    public void SummaryMessage_round_trips()
    {
        AgentMessage original = new SummaryMessage { Summary = "We discussed X and Y.", Timestamp = 55 };
        var result = RoundTrip(original);
        var summary = Assert.IsType<SummaryMessage>(result);
        Assert.Equal("We discussed X and Y.", summary.Summary);
        Assert.Equal(55, summary.Timestamp);
    }

    // --- CompletionUserContent subtypes (via UserMessage.Content / ToolResultMessage.Content) ---

    [Fact]
    public void CompletionTextContent_round_trips_as_user_content()
    {
        CompletionUserContent original = new CompletionTextContent { Text = "hello" };
        var result = RoundTrip(original);
        Assert.IsType<CompletionTextContent>(result);
    }

    [Fact]
    public void CompletionImageContent_round_trips_as_user_content()
    {
        CompletionUserContent original = new CompletionImageContent { Data = "d", MimeType = "image/png" };
        var result = RoundTrip(original);
        Assert.IsType<CompletionImageContent>(result);
    }

    [Fact]
    public void CompletionAudioInputContent_round_trips_as_user_content()
    {
        CompletionUserContent original = new CompletionAudioInputContent { Data = "d", Format = "wav" };
        var result = RoundTrip(original);
        Assert.IsType<CompletionAudioInputContent>(result);
    }

    [Fact]
    public void CompletionFileContent_round_trips_as_user_content()
    {
        CompletionUserContent original = new CompletionFileContent { Data = "d", MimeType = "text/plain" };
        var result = RoundTrip(original);
        Assert.IsType<CompletionFileContent>(result);
    }

    // --- UserMessage with mixed content ---

    [Fact]
    public void UserMessage_with_content_blocks_round_trips()
    {
        AgentMessage original = new UserMessage
        {
            Text = "See attached",
            Content =
            [
                new CompletionImageContent { Data = "img", MimeType = "image/jpeg" },
                new CompletionFileContent { Data = "file", MimeType = "text/plain", FileName = "notes.txt" },
            ],
        };

        var result = RoundTrip(original);
        var user = Assert.IsType<UserMessage>(result);
        Assert.Equal("See attached", user.Text);
        Assert.NotNull(user.Content);
        Assert.Equal(2, user.Content.Count);
        Assert.IsType<CompletionImageContent>(user.Content[0]);
        Assert.IsType<CompletionFileContent>(user.Content[1]);
    }
}
