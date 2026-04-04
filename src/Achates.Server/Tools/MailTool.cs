using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Graph;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads Outlook mail via Microsoft Graph API.
/// </summary>
internal sealed class MailTool(IReadOnlyDictionary<string, GraphClient> graphClients) : AgentTool
{
    private readonly JsonElement _schema = ObjectSchema(
        BuildSchemaProperties(graphClients),
        required: ["action"]);

    public override string Name => "mail";
    public override string Description => "Read Outlook email: list recent messages, read a specific message, search, or browse folders.";
    public override string Label => "Mail";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "list";
        var graph = ResolveClient(graphClients, arguments);

        return action switch
        {
            "list" => await ListAsync(graph, arguments, cancellationToken),
            "read" => await ReadAsync(graph, arguments, cancellationToken),
            "search" => await SearchAsync(graph, arguments, cancellationToken),
            "folders" => await FoldersAsync(graph, arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var count = GetInt(arguments, "count", 10);
        var folder = GetString(arguments, "folder") ?? "inbox";
        count = Math.Clamp(count, 1, 50);

        var path = $"mailFolders/{Uri.EscapeDataString(folder)}/messages" +
            $"?$top={count}" +
            "&$select=id,subject,from,receivedDateTime,isRead,bodyPreview" +
            "&$orderby=receivedDateTime desc";

        var result = await graph.GetAsync<GraphCollection<GraphMessage>>(path, cancellationToken);
        return FormatMessageList(result.Value, folder);
    }

    private async Task<AgentToolResult> ReadAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var messageId = GetString(arguments, "message_id");
        if (string.IsNullOrWhiteSpace(messageId))
            return TextResult("message_id is required for 'read'.");

        var path = $"messages/{Uri.EscapeDataString(messageId)}" +
            "?$select=id,subject,from,toRecipients,receivedDateTime,body";

        var msg = await graph.GetAsync<GraphMessage>(path, cancellationToken);
        return FormatFullMessage(msg);
    }

    private async Task<AgentToolResult> SearchAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("query is required for 'search'.");

        var count = GetInt(arguments, "count", 10);
        count = Math.Clamp(count, 1, 50);

        var path = $"messages" +
            $"?$search=\"{Uri.EscapeDataString(query)}\"" +
            $"&$top={count}" +
            "&$select=id,subject,from,receivedDateTime,isRead,bodyPreview";

        var result = await graph.GetAsync<GraphCollection<GraphMessage>>(path,
            new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" },
            cancellationToken);
        return FormatMessageList(result.Value, "search results");
    }

    private async Task<AgentToolResult> FoldersAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var parentFolderId = GetString(arguments, "parent_folder_id");

        var path = parentFolderId is not null
            ? $"mailFolders/{Uri.EscapeDataString(parentFolderId)}/childFolders?$top=50"
            : "mailFolders?$top=50";

        var result = await graph.GetAsync<GraphCollection<GraphMailFolder>>(path, cancellationToken);
        return FormatFolderList(result.Value, parentFolderId);
    }

    private static AgentToolResult FormatFolderList(List<GraphMailFolder> folders, string? parentId)
    {
        if (folders.Count == 0)
            return TextResult(parentId is not null ? "No child folders found." : "No mail folders found.");

        var sb = new StringBuilder();
        sb.AppendLine(parentId is not null ? "**Child folders:**" : "**Mail folders:**");
        sb.AppendLine();

        foreach (var folder in folders)
        {
            var unread = folder.UnreadItemCount > 0 ? $" ({folder.UnreadItemCount} unread)" : "";
            var children = folder.ChildFolderCount > 0 ? $" [{folder.ChildFolderCount} subfolders]" : "";
            sb.AppendLine($"- **{folder.DisplayName}** — {folder.TotalItemCount} items{unread}{children}");
            sb.AppendLine($"  ID: `{folder.Id}`");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static AgentToolResult FormatMessageList(List<GraphMessage> messages, string context)
    {
        if (messages.Count == 0)
            return TextResult($"No messages in {context}.");

        var sb = new StringBuilder();
        sb.AppendLine($"**{context}** ({messages.Count} messages):");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var read = msg.IsRead ? " " : "*";
            var from = msg.From?.EmailAddress?.ToString() ?? "Unknown";
            var date = msg.ReceivedDateTime?.LocalDateTime.ToString("g") ?? "";
            sb.AppendLine($"{read} **{msg.Subject}**");
            sb.AppendLine($"  From: {from} | {date}");
            if (!string.IsNullOrWhiteSpace(msg.BodyPreview))
                sb.AppendLine($"  {Truncate(msg.BodyPreview, 120)}");
            sb.AppendLine($"  ID: `{msg.Id}`");
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static AgentToolResult FormatFullMessage(GraphMessage msg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{msg.Subject}**");
        sb.AppendLine($"From: {msg.From?.EmailAddress}");

        if (msg.ToRecipients is { Count: > 0 })
            sb.AppendLine($"To: {string.Join(", ", msg.ToRecipients.Select(r => r.EmailAddress))}");

        sb.AppendLine($"Date: {msg.ReceivedDateTime?.LocalDateTime.ToString("g")}");
        sb.AppendLine();

        var body = msg.Body?.Content?.Trim();
        if (!string.IsNullOrEmpty(body))
            sb.AppendLine(body);

        return TextResult(sb.ToString().TrimEnd());
    }

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

    private static Dictionary<string, JsonElement> BuildSchemaProperties(
        IReadOnlyDictionary<string, GraphClient> clients)
    {
        var props = new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read", "search", "folders"], "Action to perform.", "list"),
            ["message_id"] = StringSchema("Message ID. Required for 'read'."),
            ["query"] = StringSchema("Search query (KQL syntax). Required for 'search'."),
            ["count"] = NumberSchema("Number of messages to return. Default 10, max 50."),
            ["folder"] = StringSchema("Mail folder name or ID (e.g. 'inbox', 'sentitems', 'drafts', or a folder ID). Default 'inbox'. For 'list'."),
            ["parent_folder_id"] = StringSchema("Parent folder ID to list child folders of. For 'folders'. Omit to list top-level folders."),
        };

        if (clients.Count > 1)
            props["account"] = StringEnum([.. clients.Keys], "Account to use.");

        return props;
    }

    private static GraphClient ResolveClient(
        IReadOnlyDictionary<string, GraphClient> clients,
        Dictionary<string, object?> arguments)
    {
        var account = GetString(arguments, "account");
        if (account is not null && clients.TryGetValue(account, out var named))
            return named;
        return clients.Values.First();
    }
}
