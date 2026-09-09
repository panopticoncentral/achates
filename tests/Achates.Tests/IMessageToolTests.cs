using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Graph;
using Achates.Server.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achates.Tests;

/// <summary>
/// Exercises the chat listing against a synthetic Messages database using the same
/// schema subset the real one exposes. The live database needs Full Disk Access, so
/// these build their own.
/// </summary>
public class IMessageToolTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"achates-imsg-{Guid.NewGuid():N}.db");

    private static ContactResolver EmptyContacts() =>
        new(new Dictionary<string, GraphClient>(), NullLogger.Instance);

    private void SeedDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT);
            CREATE TABLE chat (ROWID INTEGER PRIMARY KEY, chat_identifier TEXT, display_name TEXT, service_name TEXT);
            CREATE TABLE chat_handle_join (chat_id INTEGER, handle_id INTEGER);
            CREATE TABLE message (ROWID INTEGER PRIMARY KEY, text TEXT, date INTEGER, is_from_me INTEGER, handle_id INTEGER);
            CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER);

            INSERT INTO handle (ROWID, id) VALUES
                (1, '+15550001111'), (2, '+15550002222'), (3, '+15550003333');

            -- An UNNAMED group chat: display_name empty, identifier opaque.
            INSERT INTO chat (ROWID, chat_identifier, display_name, service_name)
                VALUES (10, 'chat9876543210', '', 'SMS');
            INSERT INTO chat_handle_join (chat_id, handle_id) VALUES (10,1),(10,2),(10,3);

            -- A one-to-one chat.
            INSERT INTO chat (ROWID, chat_identifier, display_name, service_name)
                VALUES (20, '+15550001111', NULL, 'iMessage');
            INSERT INTO chat_handle_join (chat_id, handle_id) VALUES (20,1);

            INSERT INTO message (ROWID, text, date, is_from_me, handle_id)
                VALUES (100, 'group message', 700000000000000000, 0, 2),
                       (200, 'direct message', 690000000000000000, 0, 1);
            INSERT INTO chat_message_join (chat_id, message_id) VALUES (10,100),(20,200);
            """;
        cmd.ExecuteNonQuery();
    }

    private static string TextOf(AgentToolResult result) =>
        string.Concat(result.Content.OfType<CompletionTextContent>().Select(c => c.Text));

    [Fact]
    public async Task Chats_listing_names_the_members_of_an_unnamed_group()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["action"] = "chats" });
        var text = TextOf(result);

        // Without the roster this chat is only "chat9876543210", and the sole way to
        // learn who is in it is to read it and infer from senders.
        Assert.Contains("Participants:", text);
        Assert.Contains("+15550002222", text);
        Assert.Contains("+15550003333", text);
    }

    [Fact]
    public async Task One_to_one_chats_do_not_get_a_participant_line()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-2", new Dictionary<string, object?> { ["action"] = "chats" });
        var text = TextOf(result);

        var directSection = text[text.IndexOf("Chat ID: `20`", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Participants:", directSection);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }
}
