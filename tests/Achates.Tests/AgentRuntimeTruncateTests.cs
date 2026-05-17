using Achates.Agent;
using Achates.Agent.Messages;

namespace Achates.Tests;

/// <summary>
/// Verifies <see cref="AgentRuntime.TruncateFromUserTurn"/> rewinds to an
/// arbitrary user-turn ordinal, dropping that prompt and everything after it.
/// </summary>
public sealed class AgentRuntimeTruncateTests
{
    private static AgentRuntime BuildRuntime(out UserMessage u0, out UserMessage u1, out UserMessage u2)
    {
        u0 = new UserMessage { Text = "first" };
        u1 = new UserMessage { Text = "second" };
        u2 = new UserMessage { Text = "third" };
        var filler = () => new ToolResultMessage { ToolCallId = "t", ToolName = "x", Content = [] };
        return new AgentRuntime(new AgentOptions
        {
            Messages = [u0, filler(), u1, filler(), u2, filler()],
        });
    }

    [Fact]
    public void TruncateFromUserTurn_FirstTurn_DropsEverything()
    {
        var runtime = BuildRuntime(out var u0, out _, out _);

        var removed = runtime.TruncateFromUserTurn(0);

        Assert.Same(u0, removed);
        Assert.Empty(runtime.Messages);
    }

    [Fact]
    public void TruncateFromUserTurn_MiddleTurn_KeepsEarlierMessages()
    {
        var runtime = BuildRuntime(out var u0, out var u1, out _);

        var removed = runtime.TruncateFromUserTurn(1);

        Assert.Same(u1, removed);
        Assert.Equal(2, runtime.Messages.Count);
        Assert.Same(u0, runtime.Messages[0]);
        Assert.IsType<ToolResultMessage>(runtime.Messages[1]);
    }

    [Fact]
    public void TruncateFromUserTurn_OutOfRange_ReturnsNullAndKeepsHistory()
    {
        var runtime = BuildRuntime(out _, out _, out _);

        Assert.Null(runtime.TruncateFromUserTurn(3));
        Assert.Null(runtime.TruncateFromUserTurn(-1));
        Assert.Equal(6, runtime.Messages.Count);
    }

    [Fact]
    public void TruncateLastUserTurn_StillRewindsOnlyTheFinalTurn()
    {
        var runtime = BuildRuntime(out _, out _, out var u2);

        var removed = runtime.TruncateLastUserTurn();

        Assert.Same(u2, removed);
        Assert.Equal(4, runtime.Messages.Count);
        Assert.DoesNotContain(runtime.Messages, m => ReferenceEquals(m, u2));
    }
}
