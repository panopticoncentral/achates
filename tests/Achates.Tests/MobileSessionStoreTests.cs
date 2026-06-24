using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class MobileSessionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"achates-test-{Guid.NewGuid():N}");
    private readonly MobileSessionStore _store;

    public MobileSessionStoreTests() => _store = new MobileSessionStore(_basePath);

    private static AssistantMessage Assistant(string text, long ts) => new()
    {
        Content = [new CompletionTextContent { Text = text }],
        Model = "m",
        Usage = new CompletionUsage { Input = 0, Output = 0, Cost = new CompletionUsageCost() },
        StopReason = CompletionStopReason.Stop,
        Timestamp = ts,
    };

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

        await _store.SaveAsync("agent1", session);
        var loaded = await _store.LoadAsync("agent1", "sess-1");

        Assert.NotNull(loaded);
        Assert.Equal("Test Session", loaded.Title);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public async Task ListAsync_ReturnsMetadataOnly()
    {
        var s1 = new MobileSession { Id = "a", Title = "First", Messages = [new UserMessage { Text = "Hi" }] };
        var s2 = new MobileSession { Id = "b", Title = "Second", Messages = [new UserMessage { Text = "Hey" }, new UserMessage { Text = "There" }] };

        await _store.SaveAsync("agent1", s1);
        await _store.SaveAsync("agent1", s2);

        var (list, hasMore) = await _store.ListAsync("agent1");
        Assert.Equal(2, list.Count);
        Assert.False(hasMore);
        Assert.Contains(list, m => m.Id == "a" && m.Title == "First" && m.MessageCount == 1);
        Assert.Contains(list, m => m.Id == "b" && m.Title == "Second" && m.MessageCount == 2);
    }

    [Fact]
    public async Task ListAsync_WithMaxValueLimit_ReturnsAllSessions()
    {
        // Regression: callers pass limit: int.MaxValue to mean "give me everything".
        // ListAsync computed Take(limit + 1) for its hasMore probe, which overflowed
        // to int.MinValue and made Take return an empty sequence — silently dropping
        // every session (this zeroed out unread counts for all agents).
        await _store.SaveAsync("agent1", new MobileSession { Id = "a", Messages = [new UserMessage { Text = "1" }] });
        await _store.SaveAsync("agent1", new MobileSession { Id = "b", Messages = [new UserMessage { Text = "2" }] });
        await _store.SaveAsync("agent1", new MobileSession { Id = "c", Messages = [new UserMessage { Text = "3" }] });

        var (list, hasMore) = await _store.ListAsync("agent1", limit: int.MaxValue);

        Assert.Equal(3, list.Count);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task ListAsync_WithWatermark_ComputesPerSessionUnread()
    {
        await _store.SaveAsync("agent1", new MobileSession
        {
            Id = "s1",
            Messages =
            [
                new UserMessage { Text = "hi", Timestamp = 100 },
                Assistant("read", 200),
                Assistant("unread", 400),
            ],
        });
        await _store.SaveAsync("agent1", new MobileSession
        {
            Id = "chatty",
            Source = SessionSource.Chat, // non-participating
            Messages = [Assistant("x", 999)],
        });

        // Watermark 300: s1 has one unread assistant (ts 400); chat session excluded.
        var (list, _) = await _store.ListAsync("agent1", limit: int.MaxValue, watermarkFor: _ => 300);

        Assert.Equal(1, list.Single(s => s.Id == "s1").Unread);
        Assert.Equal(0, list.Single(s => s.Id == "chatty").Unread);
    }

    [Fact]
    public async Task ListAsync_WithoutWatermark_UnreadIsZero()
    {
        await _store.SaveAsync("agent1", new MobileSession
        {
            Id = "s1",
            Messages = [Assistant("x", 999)],
        });

        var (list, _) = await _store.ListAsync("agent1");
        Assert.Equal(0, list.Single().Unread);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = new MobileSession { Id = "del-1", Title = "Doomed" };
        await _store.SaveAsync("agent1", session);
        await _store.DeleteAsync("agent1", "del-1");

        var loaded = await _store.LoadAsync("agent1", "del-1");
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
        await _store.SaveAsync("agent1", session);
        await _store.UpdateMetadataAsync("agent1", "meta-1", "New Title");

        var loaded = await _store.LoadAsync("agent1", "meta-1");
        Assert.Equal("New Title", loaded!.Title);
        Assert.Single(loaded.Messages);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.LoadAsync("agent1", "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNone()
    {
        var (list, _) = await _store.ListAsync("agent1");
        Assert.Empty(list);
    }

    [Fact]
    public void WithMessages_preserves_speech_enabled_and_chat_metadata()
    {
        // Regression: the end-of-turn save and chat.resubmit save both used to
        // hand-pick which fields of the existing session to carry forward,
        // and silently dropped SpeechEnabled — flipping the per-session speech
        // toggle off after every turn. (And on the resubmit path, the
        // chat-origin pairing fields too.) The WithMessages factory centralizes
        // preservation so this can't regress per-field.
        var existing = new MobileSession
        {
            Id = "sess-1",
            Title = "Existing",
            JobId = "job-42",
            Source = SessionSource.Chat,
            OriginSessionId = "origin-1",
            PeerAgentId = "peer",
            SpeechEnabled = true,
            Created = DateTimeOffset.UtcNow.AddHours(-1),
            Messages = [new UserMessage { Text = "old" }],
        };

        var next = MobileSession.WithMessages(existing, "sess-1",
            [new UserMessage { Text = "new" }, new UserMessage { Text = "newer" }]);

        Assert.True(next.SpeechEnabled);
        Assert.Equal("Existing", next.Title);
        Assert.Equal("job-42", next.JobId);
        Assert.Equal(SessionSource.Chat, next.Source);
        Assert.Equal("origin-1", next.OriginSessionId);
        Assert.Equal("peer", next.PeerAgentId);
        Assert.Equal(existing.Created, next.Created);
        Assert.Equal(2, next.Messages.Count);
    }

    [Fact]
    public void WithMessages_defaults_when_existing_is_null()
    {
        // First-turn save: there's no on-disk session yet. The factory must
        // produce a fresh session (not throw, not inherit ghost state) with
        // SpeechEnabled defaulting to false and a fresh Created stamp.
        var beforeCreate = DateTimeOffset.UtcNow;
        var next = MobileSession.WithMessages(existing: null, "fresh", [new UserMessage { Text = "hi" }]);

        Assert.Equal("fresh", next.Id);
        Assert.False(next.SpeechEnabled);
        Assert.Null(next.Title);
        Assert.Null(next.JobId);
        Assert.Null(next.Source);
        Assert.True(next.Created >= beforeCreate);
        Assert.Single(next.Messages);
    }

    [Fact]
    public async Task EndOfTurn_save_via_WithMessages_keeps_speech_enabled_across_turns()
    {
        // Integration: the simulated turn-save flow (Load existing → build new
        // via WithMessages → Save → Load) must NOT flip SpeechEnabled off.
        var initial = new MobileSession
        {
            Id = "turn-test",
            Title = "Conv",
            SpeechEnabled = true,
            Messages = [new UserMessage { Text = "first user msg" }],
        };
        await _store.SaveAsync("agent1", initial);

        // Simulate the end-of-turn save: load the existing, build a new
        // session with appended messages, save.
        var existing = await _store.LoadAsync("agent1", "turn-test");
        var next = MobileSession.WithMessages(existing, "turn-test",
            [.. existing!.Messages, new UserMessage { Text = "assistant reply" }]);
        await _store.SaveAsync("agent1", next);

        var reloaded = await _store.LoadAsync("agent1", "turn-test");
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.SpeechEnabled, "SpeechEnabled must survive the end-of-turn save");
        Assert.Equal(2, reloaded.Messages.Count);
    }
}
