using System.Text;
using System.Text.Json;
using Achates.Agent.Messages;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Mobile;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Read-only browser over the agent's own past conversation sessions: list recent
/// sessions, read a full transcript, or search across titles and message bodies.
/// The current session is always excluded. When <paramref name="since"/> is set
/// (dreamtime), listing/searching is scoped to sessions updated after that instant.
/// </summary>
internal sealed class SessionsTool(
    MobileSessionStore sessionStore,
    string agentName,
    string? currentSessionId,
    DateTimeOffset? since) : AgentTool
{
    private const int MaxScan = 200;
    private const int DefaultListLimit = 30;
    private const int DefaultSearchLimit = 10;

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read", "search"],
                "'list' shows recent sessions, 'read' loads one full transcript, 'search' finds sessions by keyword across titles and message text."),
            ["session_id"] = StringSchema("Session ID. Required for 'read'."),
            ["query"] = StringSchema("Search text. Required for 'search'."),
            ["limit"] = NumberSchema("Max results. Default 30 for 'list', 10 for 'search'."),
        },
        required: ["action"]);

    public override string Name => "sessions";
    public override string Description =>
        "Browse your own past conversation sessions. 'list' recent sessions, 'read' a full transcript by id, 'search' by keyword. Includes prior chats with users, scheduled runs, and conversations with other agents. The current session is excluded.";
    public override string Label => "Sessions";
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
            "list" => await ListAsync(GetInt(arguments, "limit") ?? DefaultListLimit, cancellationToken),
            "read" => await ReadAsync(GetString(arguments, "session_id"), cancellationToken),
            "search" => await SearchAsync(
                GetString(arguments, "query"),
                GetInt(arguments, "limit") ?? DefaultSearchLimit,
                cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    /// <summary>Recency-ordered candidates: current session excluded, scoped by <c>since</c>.</summary>
    private async Task<List<MobileSessionInfo>> CandidatesAsync(CancellationToken ct)
    {
        var (sessions, _) = await sessionStore.ListAsync(agentName, limit: MaxScan, ct: ct);
        return sessions
            .Where(s => s.Id != currentSessionId)
            .Where(s => since is null || s.Updated > since.Value)
            .ToList();
    }

    private async Task<AgentToolResult> ListAsync(int limit, CancellationToken ct)
    {
        var candidates = await CandidatesAsync(ct);
        if (candidates.Count == 0)
            return TextResult(since is null ? "No past sessions." : "No sessions since last review.");

        var shown = candidates.Take(Math.Clamp(limit, 1, MaxScan)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"**{shown.Count} session(s)** (of {candidates.Count}):");
        sb.AppendLine();
        foreach (var s in shown)
            sb.AppendLine(FormatRow(s));

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> ReadAsync(string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return TextResult("Error: 'session_id' is required.");
        if (sessionId == currentSessionId)
            return TextResult("That is the current session — you are already in it.");

        var session = await sessionStore.LoadAsync(agentName, sessionId, ct);
        if (session is null)
            return TextResult($"Session '{sessionId}' not found.");

        var sb = new StringBuilder();
        sb.AppendLine($"## Session: {session.Title ?? "(untitled)"}");
        sb.AppendLine($"Created: {session.Created.ToLocalTime():yyyy-MM-dd HH:mm} | Updated: {session.Updated.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.Append(RenderTranscript(session.Messages, toolResultMax: 300));

        return TextResult(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Two-tier search. Tier 1: substring match on title/preview metadata (no extra
    /// disk reads), ranked first. Tier 2: load remaining session bodies (recency
    /// order, bounded by <see cref="MaxScan"/>) and substring-scan message text.
    /// Results deduped by id and capped to <paramref name="limit"/>.
    /// </summary>
    private async Task<AgentToolResult> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("Error: 'query' is required.");

        limit = Math.Clamp(limit, 1, MaxScan);
        var candidates = await CandidatesAsync(ct);

        var matched = new List<(MobileSessionInfo Info, string? Snippet)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Tier 1 — cheap metadata matches.
        var bodyPool = new List<MobileSessionInfo>();
        foreach (var s in candidates)
        {
            if (Contains(s.Title, query) || Contains(s.Preview, query))
            {
                if (seen.Add(s.Id)) matched.Add((s, null));
            }
            else
            {
                bodyPool.Add(s);
            }
        }

        // Tier 2 — body scan for the rest.
        foreach (var s in bodyPool)
        {
            if (matched.Count >= limit) break;
            ct.ThrowIfCancellationRequested();

            var session = await sessionStore.LoadAsync(agentName, s.Id, ct);
            if (session is null) continue;

            var snippet = FindSnippet(session.Messages, query);
            if (snippet is not null && seen.Add(s.Id))
                matched.Add((s, snippet));
        }

        if (matched.Count == 0)
            return TextResult($"No sessions matched \"{query}\".");

        var results = matched.Take(limit).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"**{results.Count} match(es) for \"{query}\":**");
        sb.AppendLine();
        foreach (var (info, snippet) in results)
        {
            sb.AppendLine(FormatRow(info));
            if (snippet is not null)
                sb.AppendLine($"  …{snippet}…");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static string FormatRow(MobileSessionInfo s)
    {
        var title = s.Title ?? "(untitled)";
        var updated = s.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var preview = s.Preview is { Length: > 0 } p
            ? (p.Length > 100 ? p[..100] + "..." : p)
            : "";
        var row = $"- **{title}** (`{s.Id}`) — [{Origin(s)}] {s.MessageCount} messages, last active {updated}";
        return preview.Length > 0 ? $"{row}\n  {preview}" : row;
    }

    /// <summary>Origin tag derived from list metadata (no cron-store dependency).</summary>
    private static string Origin(MobileSessionInfo s) => s switch
    {
        { Source: SessionSource.Chat } => "chat",
        { CronTaskName: "Dreamtime" } => "dreamtime",
        { JobId: not null } or { CronTaskName: not null } => "cron",
        _ => "user",
    };

    private static string RenderTranscript(IReadOnlyList<AgentMessage> messages, int toolResultMax)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
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
                        sb.AppendLine($"**Tool ({toolResult.ToolName}):** {Truncate(resultText, toolResultMax)}");
                        sb.AppendLine();
                    }
                    break;
                case SummaryMessage summary:
                    sb.AppendLine($"**[Earlier conversation summary]:** {summary.Summary}");
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }

    private static string? FindSnippet(IReadOnlyList<AgentMessage> messages, string query)
    {
        foreach (var message in messages)
        {
            var text = message switch
            {
                UserMessage { Hidden: false } u => u.Text,
                AssistantMessage a => string.Join("", a.Content.OfType<CompletionTextContent>().Select(c => c.Text)),
                ToolResultMessage t => string.Join("", t.Content.OfType<CompletionTextContent>().Select(c => c.Text)),
                SummaryMessage s => s.Summary,
                _ => null,
            };
            if (string.IsNullOrEmpty(text)) continue;

            var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var start = Math.Max(0, idx - 60);
            var end = Math.Min(text.Length, idx + query.Length + 60);
            return text[start..end].Replace('\n', ' ').Trim();
        }
        return null;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static int? GetInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n) ? n : null;
        return int.TryParse(val.ToString(), out var p) ? p : null;
    }
}
