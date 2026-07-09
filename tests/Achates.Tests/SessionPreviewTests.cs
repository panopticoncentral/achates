using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;

namespace Achates.Tests;

/// <summary>
/// Pins the session-list preview text. The hidden <c>[Scheduled task: ...]</c>
/// marker that every cron/dreamtime session carries as its first (hidden) user
/// message must never surface as the row subtitle.
/// </summary>
public sealed class SessionPreviewTests
{
    private static AssistantMessage Assistant(string text) => new()
    {
        Content = [new CompletionTextContent { Text = text }],
        Model = "m",
        Usage = new CompletionUsage { Input = 0, Output = 0, Cost = new CompletionUsageCost() },
        StopReason = CompletionStopReason.Stop,
    };

    [Fact]
    public void SkipsHiddenScheduledTaskMarker()
    {
        var session = new MobileSession
        {
            Id = "s",
            Messages =
            [
                new UserMessage { Text = "[Scheduled task: Dreamtime]\nConsolidate memory.", Hidden = true },
                Assistant("Reviewed today's sessions and updated memory."),
            ],
        };

        Assert.Equal("Reviewed today's sessions and updated memory.", SessionPreview.Compute(session));
    }

    [Fact]
    public void UsesLatestVisibleMessage()
    {
        var session = new MobileSession
        {
            Id = "s",
            Messages = [new UserMessage { Text = "hello" }, Assistant("hi there")],
        };

        Assert.Equal("hi there", SessionPreview.Compute(session));
    }

    [Fact]
    public void CollapsesWhitespace()
    {
        var session = new MobileSession
        {
            Id = "s",
            Messages = [Assistant("line one\n\nline   two")],
        };

        Assert.Equal("line one line two", SessionPreview.Compute(session));
    }

    [Fact]
    public void ReturnsNullWhenOnlyHiddenMarker()
    {
        var session = new MobileSession
        {
            Id = "s",
            Messages = [new UserMessage { Text = "[Scheduled task: Dreamtime]", Hidden = true }],
        };

        Assert.Null(SessionPreview.Compute(session));
    }
}
