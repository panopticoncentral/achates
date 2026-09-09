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
            CREATE TABLE message (ROWID INTEGER PRIMARY KEY, text TEXT, date INTEGER, is_from_me INTEGER, handle_id INTEGER, attributedBody BLOB);
            CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER);
            CREATE TABLE attachment (ROWID INTEGER PRIMARY KEY, filename TEXT, mime_type TEXT, uti TEXT);
            CREATE TABLE message_attachment_join (message_id INTEGER, attachment_id INTEGER);

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

        // Most real messages leave `text` NULL and carry their content in
        // `attributedBody`; one of these is undecodable to stand in for an
        // archive shape the decoder does not understand.
        InsertAttributedBody(conn, rowId: 300, chatId: 10, date: 710000000000000000,
            body: TypedstreamFixture.Build(AttributedOnlyText));
        InsertAttributedBody(conn, rowId: 400, chatId: 10, date: 705000000000000000,
            body: [0x04, 0x0b, 0x99, 0x99]);

        // A message carrying more than one attachment: the join against the
        // attachment tables yields a row per attachment, not per message.
        InsertWithAttachments(conn, rowId: 500, chatId: 10, date: 715000000000000000,
            text: MultiAttachmentText,
            attachments:
            [
                (1, "~/Library/Messages/Attachments/note.caf", "audio/x-caf", "com.apple.coreaudio-format"),
                (2, "~/Library/Messages/Attachments/photo.jpg", "image/jpeg", "public.jpeg"),
            ]);
    }

    private const string MultiAttachmentText = "voice note plus a photo";

    private static void InsertWithAttachments(
        SqliteConnection conn, long rowId, long chatId, long date, string text,
        (long Id, string Filename, string Mime, string Uti)[] attachments)
    {
        using var msg = conn.CreateCommand();
        msg.CommandText = """
            INSERT INTO message (ROWID, text, date, is_from_me, handle_id)
                VALUES (@rowId, @text, @date, 0, 2);
            INSERT INTO chat_message_join (chat_id, message_id) VALUES (@chatId, @rowId);
            """;
        msg.Parameters.AddWithValue("@rowId", rowId);
        msg.Parameters.AddWithValue("@chatId", chatId);
        msg.Parameters.AddWithValue("@date", date);
        msg.Parameters.AddWithValue("@text", text);
        msg.ExecuteNonQuery();

        foreach (var (id, filename, mime, uti) in attachments)
        {
            using var att = conn.CreateCommand();
            att.CommandText = """
                INSERT INTO attachment (ROWID, filename, mime_type, uti)
                    VALUES (@id, @filename, @mime, @uti);
                INSERT INTO message_attachment_join (message_id, attachment_id)
                    VALUES (@rowId, @id);
                """;
            att.Parameters.AddWithValue("@id", id);
            att.Parameters.AddWithValue("@filename", filename);
            att.Parameters.AddWithValue("@mime", mime);
            att.Parameters.AddWithValue("@uti", uti);
            att.Parameters.AddWithValue("@rowId", rowId);
            att.ExecuteNonQuery();
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private const string AttributedOnlyText = "Sure—the deadline moved to Tuesday afternoon";

    private static void InsertAttributedBody(
        SqliteConnection conn, long rowId, long chatId, long date, byte[] body)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO message (ROWID, text, date, is_from_me, handle_id, attributedBody)
                VALUES (@rowId, NULL, @date, 0, 2, @body);
            INSERT INTO chat_message_join (chat_id, message_id) VALUES (@chatId, @rowId);
            """;
        cmd.Parameters.AddWithValue("@rowId", rowId);
        cmd.Parameters.AddWithValue("@chatId", chatId);
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@body", body);
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

    [Fact]
    public async Task Read_surfaces_a_message_stored_only_in_attributed_body()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-3", new Dictionary<string, object?>
        {
            ["action"] = "read",
            ["chat_id"] = 10L,
        });

        Assert.Contains(AttributedOnlyText, TextOf(result));
    }

    [Fact]
    public async Task Read_marks_an_undecodable_message_instead_of_dropping_it()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-4", new Dictionary<string, object?>
        {
            ["action"] = "read",
            ["chat_id"] = 10L,
        });

        // Dropping it silently hands the agent a transcript with an invisible hole.
        Assert.Contains("[unreadable message]", TextOf(result));
    }

    [Fact]
    public async Task Search_finds_a_message_stored_only_in_attributed_body()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-5", new Dictionary<string, object?>
        {
            ["action"] = "search",
            ["query"] = "deadline moved",
        });

        Assert.Contains(AttributedOnlyText, TextOf(result));
    }

    [Fact]
    public async Task Read_returns_a_multi_attachment_message_once()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        var result = await tool.ExecuteAsync("call-6", new Dictionary<string, object?>
        {
            ["action"] = "read",
            ["chat_id"] = 10L,
        });

        Assert.Equal(1, Occurrences(TextOf(result), MultiAttachmentText));
    }

    [Fact]
    public async Task Read_count_limits_distinct_messages_not_joined_rows()
    {
        SeedDatabase();
        var tool = new IMessageTool(_dbPath, EmptyContacts());

        // Chat 10 holds four messages; the newest carries two attachments, so a
        // per-attachment row count silently returns one message fewer.
        var result = await tool.ExecuteAsync("call-7", new Dictionary<string, object?>
        {
            ["action"] = "read",
            ["chat_id"] = 10L,
            ["count"] = 4,
        });
        var text = TextOf(result);

        Assert.Equal(1, Occurrences(text, MultiAttachmentText));
        Assert.Contains(AttributedOnlyText, text);
        Assert.Contains("[unreadable message]", text);
        Assert.Contains("group message", text);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }
}
