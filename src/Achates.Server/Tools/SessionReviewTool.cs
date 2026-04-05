using System.Text;
using System.Text.Json;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Read-only session browser for dreamtime. Lists sessions updated since the last
/// dreamtime run and reads full session transcripts.
/// </summary>
internal sealed class SessionReviewTool(
    MobileSessionStore sessionStore,
    string agentName,
    DateTimeOffset? since) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read"],
                "Action to perform. 'list' shows sessions updated since the last dreamtime. 'read' loads a full session transcript."),
            ["session_id"] = StringSchema("Session ID. Required for 'read'."),
        },
        required: ["action"]);

    public override string Name => "session_review";
    public override string Description =>
        "Review past conversation sessions. Use 'list' to see recent sessions, then 'read' to load interesting ones in full.";
    public override string Label => "Session Review";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(cancellationToken),
            "read" => await ReadAsync(GetString(arguments, "session_id"), cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListAsync(CancellationToken ct)
    {
        var (sessions, _) = await sessionStore.ListAsync(agentName, limit: 200, ct: ct);

        // Filter to sessions updated since last dreamtime
        var filtered = since is not null
            ? sessions.Where(s => s.Updated > since.Value).ToList()
            : sessions;

        if (filtered.Count == 0)
            return TextResult("No sessions to review since last dreamtime.");

        var sb = new StringBuilder();
        sb.AppendLine($"**{filtered.Count} session(s) since last dreamtime:**");
        sb.AppendLine();

        foreach (var session in filtered)
        {
            var title = session.Title ?? "(untitled)";
            var updated = session.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var preview = session.Preview is { Length: > 0 } p
                ? (p.Length > 100 ? p[..100] + "..." : p)
                : "";

            sb.AppendLine($"- **{title}** (`{session.Id}`) — {session.MessageCount} messages, last active {updated}");
            if (preview.Length > 0)
                sb.AppendLine($"  {preview}");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> ReadAsync(string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return TextResult("Error: 'session_id' is required.");

        var session = await sessionStore.LoadAsync(agentName, sessionId, ct);
        if (session is null)
            return TextResult($"Session '{sessionId}' not found.");

        var sb = new StringBuilder();
        sb.AppendLine($"## Session: {session.Title ?? "(untitled)"}");
        sb.AppendLine($"Created: {session.Created.ToLocalTime():yyyy-MM-dd HH:mm} | Updated: {session.Updated.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var message in session.Messages)
        {
            switch (message)
            {
                case UserMessage { Hidden: false } user:
                    sb.AppendLine($"**User:** {user.Text}");
                    sb.AppendLine();
                    break;
                case AssistantMessage assistant:
                    var text = string.Join("", assistant.Content
                        .OfType<CompletionTextContent>()
                        .Select(c => c.Text));
                    if (text.Length > 0)
                    {
                        sb.AppendLine($"**Assistant:** {text}");
                        sb.AppendLine();
                    }
                    break;
                case ToolResultMessage toolResult:
                    var resultText = string.Join("", toolResult.Content
                        .OfType<CompletionTextContent>()
                        .Select(c => c.Text));
                    if (resultText.Length > 0)
                    {
                        sb.AppendLine($"**Tool ({toolResult.ToolName}):** {Truncate(resultText, 300)}");
                        sb.AppendLine();
                    }
                    break;
                case SummaryMessage summary:
                    sb.AppendLine($"**[Earlier conversation summary]:** {summary.Summary}");
                    sb.AppendLine();
                    break;
            }
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
