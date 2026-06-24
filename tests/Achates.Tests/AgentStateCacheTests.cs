using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class AgentStateCacheTests
{
    [Fact]
    public void Get_ReturnsNull_WhenNotCached()
    {
        var cache = new AgentStateCache();
        Assert.Null(cache.Get("agent1"));
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedState()
    {
        var cache = new AgentStateCache();
        var state = new AgentPreviewState("Hello world", "2026-03-31T00:00:00Z", 3);
        cache.Set("agent1", state);

        var result = cache.Get("agent1");
        Assert.NotNull(result);
        Assert.Equal("Hello world", result.LastMessage);
        Assert.Equal(3, result.UnreadCount);
    }

    [Fact]
    public void Invalidate_RemovesCachedState()
    {
        var cache = new AgentStateCache();
        cache.Set("agent1", new AgentPreviewState("Hi", "2026-03-31T00:00:00Z", 1));
        cache.Invalidate("agent1");

        Assert.Null(cache.Get("agent1"));
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        var cache = new AgentStateCache();
        cache.Set("agent1", new AgentPreviewState("Hi", "2026-03-31T00:00:00Z", 1));
        cache.Set("agent2", new AgentPreviewState("Hey", "2026-03-31T00:00:00Z", 2));
        cache.InvalidateAll();

        Assert.Null(cache.Get("agent1"));
        Assert.Null(cache.Get("agent2"));
    }
}
