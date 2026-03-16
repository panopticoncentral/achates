using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Microsoft.Data.Sqlite;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads iMessage conversations from the local macOS Messages database (read-only).
/// Requires Full Disk Access for the host process.
/// </summary>
internal sealed class IMessageTool(string dbPath, ContactResolver contacts) : AgentTool
{
    // macOS Core Data epoch: 2001-01-01 00:00:00 UTC
    private static readonly DateTimeOffset CoreDataEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["chats", "read", "search"], "Action to perform.", "chats"),
            ["chat_id"] = NumberSchema("Chat row ID. Required for 'read'."),
            ["query"] = StringSchema("Search text. Required for 'search'."),
            ["count"] = NumberSchema("Number of results to return. Default 20, max 50."),
        },
        required: ["action"]);

    public override string Name => "imessage";
    public override string Description => "Read iMessage conversations: list recent chats, read messages from a chat, or search.";
    public override string Label => "iMessage";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "chats";

        try
        {
            await contacts.EnsureLoadedAsync(cancellationToken);

            return action switch
            {
                "chats" => await ListChatsAsync(arguments, cancellationToken),
                "read" => await ReadChatAsync(arguments, cancellationToken),
                "search" => await SearchAsync(arguments, cancellationToken),
                _ => TextResult($"Unknown action: {action}"),
            };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 14 or 23) // CANTOPEN or AUTH
        {
            return TextResult("Cannot open Messages database. Ensure Full Disk Access is granted for the running process.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5) // BUSY
        {
            return TextResult("Messages database is busy (locked by another process). Try again in a moment.");
        }
    }

    private async Task<AgentToolResult> ListChatsAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var count = Math.Clamp(GetInt(arguments, "count", 20), 1, 50);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        // Use MAX(m.date) aggregate to find the last message per chat efficiently,
        // avoiding correlated subqueries that are too slow on large databases.
        cmd.CommandText = """
            SELECT
                c.ROWID,
                c.chat_identifier,
                c.display_name,
                c.service_name,
                latest.last_date,
                m.text as last_message,
                m.is_from_me as last_is_from_me
            FROM chat c
            INNER JOIN (
                SELECT cmj.chat_id, MAX(m2.date) as last_date, MAX(m2.ROWID) as last_msg_id
                FROM chat_message_join cmj
                INNER JOIN message m2 ON cmj.message_id = m2.ROWID
                GROUP BY cmj.chat_id
            ) latest ON latest.chat_id = c.ROWID
            LEFT JOIN message m ON m.ROWID = latest.last_msg_id
            ORDER BY latest.last_date DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"**Recent chats** (up to {count}):");
        sb.AppendLine();

        while (await reader.ReadAsync(cancellationToken))
        {
            var chatId = reader.GetInt64(0);
            var identifier = reader.GetString(1);
            var displayName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var service = reader.IsDBNull(3) ? null : reader.GetString(3);
            var lastDate = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
            var lastText = reader.IsDBNull(5) ? null : reader.GetString(5);
            var lastIsFromMe = !reader.IsDBNull(6) && reader.GetInt64(6) == 1;

            var label = !string.IsNullOrWhiteSpace(displayName) ? displayName : contacts.Resolve(identifier);
            var serviceTag = service switch
            {
                "iMessage" => "iMessage",
                "SMS" => "SMS",
                _ => service ?? "",
            };

            sb.AppendLine($"**{label}** [{serviceTag}]");
            if (lastText is not null)
            {
                var preview = lastIsFromMe ? $"You: {Truncate(lastText, 100)}" : Truncate(lastText, 100);
                var dateStr = lastDate.HasValue ? FormatDate(lastDate.Value) : "";
                sb.AppendLine($"  {preview}");
                if (dateStr.Length > 0)
                    sb.AppendLine($"  {dateStr}");
            }
            sb.AppendLine($"  Chat ID: `{chatId}`");
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> ReadChatAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var chatId = GetLong(arguments, "chat_id");
        if (chatId is null)
            return TextResult("chat_id is required for 'read'.");

        var count = Math.Clamp(GetInt(arguments, "count", 20), 1, 50);

        await using var conn = OpenConnection();

        // Get chat info
        await using var infoCmd = conn.CreateCommand();
        infoCmd.CommandText = """
            SELECT chat_identifier, display_name FROM chat WHERE ROWID = @chatId
            """;
        infoCmd.Parameters.AddWithValue("@chatId", chatId.Value);
        await using var infoReader = await infoCmd.ExecuteReaderAsync(cancellationToken);

        string chatLabel;
        if (await infoReader.ReadAsync(cancellationToken))
        {
            var displayName = infoReader.IsDBNull(1) ? null : infoReader.GetString(1);
            chatLabel = !string.IsNullOrWhiteSpace(displayName) ? displayName : contacts.Resolve(infoReader.GetString(0));
        }
        else
        {
            return TextResult($"Chat {chatId} not found.");
        }
        // Get messages with audio attachment info (most recent N, displayed oldest-first)
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = """
            SELECT
                m.ROWID,
                m.text,
                m.is_from_me,
                m.date,
                h.id as sender,
                a.filename as audio_path
            FROM message m
            INNER JOIN chat_message_join cmj ON cmj.message_id = m.ROWID
            LEFT JOIN handle h ON m.handle_id = h.ROWID
            LEFT JOIN message_attachment_join maj ON maj.message_id = m.ROWID
            LEFT JOIN attachment a ON maj.attachment_id = a.ROWID
                AND (a.mime_type LIKE 'audio/%' OR a.uti LIKE '%audio%' OR a.uti LIKE '%caf%')
            WHERE cmj.chat_id = @chatId
            ORDER BY m.date DESC
            LIMIT @count
            """;
        msgCmd.Parameters.AddWithValue("@chatId", chatId.Value);
        msgCmd.Parameters.AddWithValue("@count", count);

        await using var reader = await msgCmd.ExecuteReaderAsync(cancellationToken);
        var messages = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var text = reader.IsDBNull(1) ? null : reader.GetString(1);
            var isFromMe = reader.GetInt64(2) == 1;
            var date = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            var sender = reader.IsDBNull(4) ? null : reader.GetString(4);
            var audioPath = reader.IsDBNull(5) ? null : reader.GetString(5);

            if (text is null && audioPath is null) continue; // skip non-text, non-audio attachments

            var dateStr = date.HasValue ? FormatDate(date.Value) : "";
            var who = isFromMe ? "You" : contacts.Resolve(sender);

            var content = text ?? "";
            if (audioPath is not null)
            {
                var expandedPath = ExpandHome(audioPath);
                if (text is not null)
                    content += $"\n  [Voice message: `{expandedPath}`]";
                else
                    content = $"[Voice message: `{expandedPath}`]";
            }

            messages.Add($"[{dateStr}] **{who}**: {content}");
        }

        messages.Reverse(); // oldest first

        var sb = new StringBuilder();
        sb.AppendLine($"**{chatLabel}** (last {messages.Count} messages):");
        sb.AppendLine();
        foreach (var msg in messages)
            sb.AppendLine(msg);

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> SearchAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("query is required for 'search'.");

        var count = Math.Clamp(GetInt(arguments, "count", 20), 1, 50);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                m.ROWID,
                m.text,
                m.is_from_me,
                m.date,
                h.id as sender,
                c.ROWID as chat_id,
                c.chat_identifier,
                c.display_name,
                a.filename as audio_path
            FROM message m
            INNER JOIN chat_message_join cmj ON cmj.message_id = m.ROWID
            INNER JOIN chat c ON cmj.chat_id = c.ROWID
            LEFT JOIN handle h ON m.handle_id = h.ROWID
            LEFT JOIN message_attachment_join maj ON maj.message_id = m.ROWID
            LEFT JOIN attachment a ON maj.attachment_id = a.ROWID
                AND (a.mime_type LIKE 'audio/%' OR a.uti LIKE '%audio%' OR a.uti LIKE '%caf%')
            WHERE m.text LIKE @query
            ORDER BY m.date DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@query", $"%{query}%");
        cmd.Parameters.AddWithValue("@count", count);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"**Search results for \"{query}\":**");
        sb.AppendLine();

        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            found = true;
            var text = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var isFromMe = reader.GetInt64(2) == 1;
            var date = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            var sender = reader.IsDBNull(4) ? null : reader.GetString(4);
            var chatId = reader.GetInt64(5);
            var chatIdentifier = reader.GetString(6);
            var displayName = reader.IsDBNull(7) ? null : reader.GetString(7);
            var audioPath = reader.IsDBNull(8) ? null : reader.GetString(8);

            var chatLabel = !string.IsNullOrWhiteSpace(displayName) ? displayName : contacts.Resolve(chatIdentifier);
            var who = isFromMe ? "You" : contacts.Resolve(sender);
            var dateStr = date.HasValue ? FormatDate(date.Value) : "";

            var content = text;
            if (audioPath is not null)
                content += $" [Voice message: `{ExpandHome(audioPath)}`]";

            sb.AppendLine($"**{who}** in *{chatLabel}* [{dateStr}]:");
            sb.AppendLine($"  {Truncate(content, 200)}");
            sb.AppendLine($"  Chat ID: `{chatId}`");
            sb.AppendLine();
        }

        if (!found)
            return TextResult($"No messages found matching \"{query}\".");

        return TextResult(sb.ToString().TrimEnd());
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.DefaultTimeout = 15; // seconds — prevent indefinite hangs on large databases
        conn.Open();

        // Prevent indefinite hangs on WAL locks
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    private static string FormatDate(long coreDataTimestamp)
    {
        // macOS Messages uses nanoseconds since 2001-01-01 UTC
        var seconds = coreDataTimestamp / 1_000_000_000.0;
        var dt = CoreDataEpoch.AddSeconds(seconds);
        return TimeZoneInfo.ConvertTime(dt, TimeZoneInfo.Local).ToString("g");
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static int GetInt(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return defaultValue;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : defaultValue;
        return val is int i ? i : defaultValue;
    }

    private static long? GetLong(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number ? je.GetInt64() : null;
        return val is long l ? l : null;
    }
}
