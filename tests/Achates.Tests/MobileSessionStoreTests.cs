using Achates.Agent.Messages;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class MobileSessionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"achates-test-{Guid.NewGuid():N}");
    private readonly MobileSessionStore _store;

    public MobileSessionStoreTests() => _store = new MobileSessionStore(_basePath);

    public void Dispose()
    {
        if (Directory.Exists(_basePath)) Directory.Delete(_basePath, true);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var session = new MobileSession
        {
            Id = "sess-1",
            Title = "Test Session",
            Messages = [new UserMessage { Text = "Hello" }]
        };

        await _store.SaveAsync("agent1", "peer1", session);
        var loaded = await _store.LoadAsync("agent1", "peer1", "sess-1");

        Assert.NotNull(loaded);
        Assert.Equal("Test Session", loaded.Title);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public async Task ListAsync_ReturnsMetadataOnly()
    {
        var s1 = new MobileSession { Id = "a", Title = "First", Messages = [new UserMessage { Text = "Hi" }] };
        var s2 = new MobileSession { Id = "b", Title = "Second", Messages = [new UserMessage { Text = "Hey" }, new UserMessage { Text = "There" }] };

        await _store.SaveAsync("agent1", "peer1", s1);
        await _store.SaveAsync("agent1", "peer1", s2);

        var list = await _store.ListAsync("agent1", "peer1");
        Assert.Equal(2, list.Count);
        Assert.Contains(list, m => m.Id == "a" && m.Title == "First" && m.MessageCount == 1);
        Assert.Contains(list, m => m.Id == "b" && m.Title == "Second" && m.MessageCount == 2);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = new MobileSession { Id = "del-1", Title = "Doomed" };
        await _store.SaveAsync("agent1", "peer1", session);
        await _store.DeleteAsync("agent1", "peer1", "del-1");

        var loaded = await _store.LoadAsync("agent1", "peer1", "del-1");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task UpdateMetadataAsync_UpdatesTitleOnly()
    {
        var session = new MobileSession
        {
            Id = "meta-1",
            Title = "Old Title",
            Messages = [new UserMessage { Text = "Keep me" }]
        };
        await _store.SaveAsync("agent1", "peer1", session);
        await _store.UpdateMetadataAsync("agent1", "peer1", "meta-1", "New Title");

        var loaded = await _store.LoadAsync("agent1", "peer1", "meta-1");
        Assert.Equal("New Title", loaded!.Title);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.LoadAsync("agent1", "peer1", "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNone()
    {
        var list = await _store.ListAsync("agent1", "peer1");
        Assert.Empty(list);
    }
}
