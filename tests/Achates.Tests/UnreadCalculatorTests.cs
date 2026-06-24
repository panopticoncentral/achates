using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class UnreadCalculatorTests
{
    private static MobileSession Session(SessionSource? source, params AgentMessage[] msgs)
        => new() { Id = "s", Source = source, Messages = [.. msgs] };

    private static AssistantMessage Assistant(long ts) => new()
    {
        Content = [new CompletionTextContent { Text = "a" }],
        Model = "m",
        Usage = new CompletionUsage { Input = 0, Output = 0, Cost = new CompletionUsageCost() },
        StopReason = CompletionStopReason.Stop,
        Timestamp = ts,
    };

    [Fact]
    public void Participates_ExcludesChatAndDreamtime()
    {
        Assert.False(UnreadCalculator.Participates(SessionSource.Chat, null));
        Assert.False(UnreadCalculator.Participates(null, "Dreamtime"));
        Assert.True(UnreadCalculator.Participates(null, null));
        Assert.True(UnreadCalculator.Participates(null, "Morning Brief"));
    }

    [Fact]
    public void UnreadFor_CountsAssistantMessagesNewerThanWatermark()
    {
        var s = Session(null,
            new UserMessage { Text = "hi", Timestamp = 100 },
            Assistant(200),
            Assistant(400),
            Assistant(500));

        Assert.Equal(2, UnreadCalculator.UnreadFor(s, cronTaskName: null, watermark: 300));
        Assert.Equal(0, UnreadCalculator.UnreadFor(s, cronTaskName: null, watermark: 500));
    }

    [Fact]
    public void UnreadFor_NonParticipatingSession_IsAlwaysZero()
    {
        var s = Session(SessionSource.Chat, Assistant(999));
        Assert.Equal(0, UnreadCalculator.UnreadFor(s, cronTaskName: null, watermark: 0));

        var d = Session(null, Assistant(999));
        Assert.Equal(0, UnreadCalculator.UnreadFor(d, cronTaskName: "Dreamtime", watermark: 0));
    }

    [Fact]
    public void CaughtUpTimestamp_IsMaxOverAllMessageRoles()
    {
        var s = Session(null,
            Assistant(400),
            new UserMessage { Text = "trailing", Timestamp = 600 });
        Assert.Equal(600, UnreadCalculator.CaughtUpTimestamp(s));
    }

    [Fact]
    public void CaughtUpTimestamp_EmptySession_IsZero()
        => Assert.Equal(0, UnreadCalculator.CaughtUpTimestamp(new MobileSession { Id = "s" }));
}
