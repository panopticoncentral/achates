using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Messages;

namespace Achates.Tests;

public sealed class AgentSpeechMessageTests
{
    [Fact]
    public void Roundtrips_through_polymorphic_AgentMessage_serialization()
    {
        AgentMessage msg = new AgentSpeechMessage
        {
            SpeakerAgentId = "val",
            SpeakerDisplayName = "Val",
            ToAgentId = "claire",
            Text = "hello",
        };

        var json = JsonSerializer.Serialize(msg);
        Assert.Contains("\"role\":\"speech\"", json);

        var back = JsonSerializer.Deserialize<AgentMessage>(json);
        var speech = Assert.IsType<AgentSpeechMessage>(back);
        Assert.Equal("val", speech.SpeakerAgentId);
        Assert.Equal("claire", speech.ToAgentId);
        Assert.Equal("hello", speech.Text);
    }

    [Fact]
    public void Is_excluded_from_llm_context()
    {
        IReadOnlyList<AgentMessage> history =
        [
            new UserMessage { Text = "hi" },
            new AgentSpeechMessage
            {
                SpeakerAgentId = "claire", SpeakerDisplayName = "Claire",
                ToAgentId = "val", Text = "secret side channel",
            },
        ];

        // Guards against a future conversion arm leaking AgentSpeechMessage into
        // LLM context (the explicit skip case is structural documentation).
        var llm = MessageConversion.DefaultConvertToLlm(history);

        Assert.DoesNotContain(llm, m => m.ToString()!.Contains("secret side channel"));
        Assert.Single(llm);
    }
}
