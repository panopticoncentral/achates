using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void ChatSessionId_is_deterministic_per_origin_and_target()
    {
        var a = MobileSessionStore.ChatSessionId("sess-1", "claire");
        var b = MobileSessionStore.ChatSessionId("sess-1", "claire");
        var c = MobileSessionStore.ChatSessionId("sess-1", "val");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("chat-", a);
        Assert.Equal(5 + 12, a.Length);
    }

    [Fact]
    public async Task LoadOrCreateChatSession_creates_then_reuses_same_file()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "achates-chatsess-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new MobileSessionStore(tempBase);
            var s1 = await store.LoadOrCreateChatSessionAsync("claire", "sess-1", "val");
            Assert.Equal(SessionSource.Chat, s1.Source);
            Assert.Equal("sess-1", s1.OriginSessionId);
            Assert.Equal("val", s1.PeerAgentId);

            s1.Messages.Add(new Achates.Agent.Messages.UserMessage { Text = "x" });
            await store.SaveAsync("claire", s1);

            var s2 = await store.LoadOrCreateChatSessionAsync("claire", "sess-1", "val");
            Assert.Equal(s1.Id, s2.Id);
            Assert.Single(s2.Messages);

            var sessionFiles = Directory.GetFiles(
                Path.Combine(tempBase, "agents", "claire", "sessions"), "*.json");
            Assert.Single(sessionFiles);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true);
        }
    }
}
