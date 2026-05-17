using Achates.Agent.Messages;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ChatTranscriptBufferTests
{
    [Fact]
    public void Splices_buffered_speech_after_matching_tool_result()
    {
        var buffer = new ChatTranscriptBuffer();
        buffer.Add("tc-1", new AgentSpeechMessage
        { SpeakerAgentId = "val", SpeakerDisplayName = "Val", ToAgentId = "claire", Text = "q" });
        buffer.Add("tc-1", new AgentSpeechMessage
        { SpeakerAgentId = "claire", SpeakerDisplayName = "Claire", ToAgentId = "val", Text = "a" });

        var runtimeMsgs = new List<AgentMessage>
        {
            new UserMessage { Text = "user asked" },
            new AssistantMessage { Content = [], Model = "m",
                Usage = Achates.Providers.Completions.CompletionUsage.Empty,
                StopReason = Achates.Providers.Completions.CompletionStopReason.ToolUse },
            new ToolResultMessage { ToolCallId = "tc-1", ToolName = "chat", Content = [] },
            new AssistantMessage { Content = [], Model = "m",
                Usage = Achates.Providers.Completions.CompletionUsage.Empty,
                StopReason = Achates.Providers.Completions.CompletionStopReason.Stop },
        };

        var merged = buffer.Merge(runtimeMsgs);

        Assert.Equal(6, merged.Count);
        Assert.IsType<ToolResultMessage>(merged[2]);
        Assert.IsType<AgentSpeechMessage>(merged[3]);
        Assert.IsType<AgentSpeechMessage>(merged[4]);
        Assert.Equal("q", ((AgentSpeechMessage)merged[3]).Text);
        Assert.IsType<AssistantMessage>(merged[5]);
    }

    [Fact]
    public void Merge_is_noop_when_empty()
    {
        var buffer = new ChatTranscriptBuffer();
        var msgs = new List<AgentMessage> { new UserMessage { Text = "x" } };
        Assert.Single(buffer.Merge(msgs));
    }
}
