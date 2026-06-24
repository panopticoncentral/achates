using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ReadStateStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"achates-test-{Guid.NewGuid():N}");
    private readonly ReadStateStore _store;

    public ReadStateStoreTests() => _store = new ReadStateStore(_basePath);

    public void Dispose()
    {
        if (Directory.Exists(_basePath)) Directory.Delete(_basePath, true);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyState()
    {
        var state = await _store.LoadAsync("agent1");
        Assert.Empty(state.Sessions);
        Assert.Null(state.LastReadTimestamp);
        Assert.Equal(0, state.Watermark("any"));
    }

    [Fact]
    public async Task LoadAsync_LegacyFile_PopulatesFloorAndUsesItAsWatermark()
    {
        var dir = Path.Combine(_basePath, "agents", "agent1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "read-state.json"), """{"last_read_timestamp":1000}""");

        var state = await _store.LoadAsync("agent1");
        Assert.Equal(1000, state.LastReadTimestamp);
        Assert.Empty(state.Sessions);
        Assert.Equal(1000, state.Watermark("unseen-session"));
    }

    [Fact]
    public async Task AdvanceSessionReadAsync_IsForwardOnlyPerSession()
    {
        await _store.AdvanceSessionReadAsync("agent1", "s1", 500);
        await _store.AdvanceSessionReadAsync("agent1", "s1", 300);
        var state = await _store.LoadAsync("agent1");
        Assert.Equal(500, state.Watermark("s1"));

        await _store.AdvanceSessionReadAsync("agent1", "s1", 900);
        state = await _store.LoadAsync("agent1");
        Assert.Equal(900, state.Watermark("s1"));
    }

    [Fact]
    public async Task PerSessionEntry_OverridesLegacyFloor()
    {
        var dir = Path.Combine(_basePath, "agents", "agent1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "read-state.json"), """{"last_read_timestamp":1000}""");

        await _store.AdvanceSessionReadAsync("agent1", "s1", 50);
        var state = await _store.LoadAsync("agent1");
        Assert.Equal(50, state.Watermark("s1"));
        Assert.Equal(1000, state.Watermark("s2"));
    }

    [Fact]
    public async Task RemoveSessionAsync_DropsEntryAndFallsBackToLegacyFloor()
    {
        var dir = Path.Combine(_basePath, "agents", "agent1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "read-state.json"), """{"last_read_timestamp":500}""");

        await _store.AdvanceSessionReadAsync("agent1", "s1", 900);
        await _store.RemoveSessionAsync("agent1", "s1");

        var state = await _store.LoadAsync("agent1");
        Assert.False(state.Sessions.ContainsKey("s1"));
        Assert.Equal(500, state.Watermark("s1")); // falls back to legacy floor
    }

    [Fact]
    public async Task AdvanceLegacyAsync_IsForwardOnly()
    {
        await _store.AdvanceLegacyAsync("agent1", 500);
        await _store.AdvanceLegacyAsync("agent1", 300);
        var state = await _store.LoadAsync("agent1");
        Assert.Equal(500, state.LastReadTimestamp);
    }
}
