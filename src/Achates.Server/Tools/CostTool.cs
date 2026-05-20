using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Queries the persistent cost ledger for usage summaries, recent entries, and breakdowns.
/// Supports per-agent (self), specific-agent, and cross-agent ("all") scopes.
/// </summary>
internal sealed class CostTool(
    string callingAgent,
    IReadOnlyDictionary<string, CostLedger> ledgers) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["summary", "recent", "breakdown"],
                "Action to perform: summary (totals), recent (last N entries), breakdown (grouped)."),
            ["scope"] = StringSchema(
                "Whose costs to query: \"self\" (the calling agent, default), \"all\" (every agent), or a specific agent name."),
            ["period"] = StringEnum(["today", "week", "month", "all"],
                "Time period to query.", "today"),
            ["count"] = NumberSchema("Number of recent entries to return. Default 10. Only used with 'recent' action."),
            ["group_by"] = StringEnum(["day", "model", "agent", "channel", "peer"],
                "Grouping for breakdown action. 'agent' is most useful with scope='all'; 'channel' separates direct turns vs inter-agent chat vs cron; 'peer' surfaces initiator agents and job ids.",
                "day"),
        },
        required: ["action"]);

    public override string Name => "cost";
    public override string Description => "Query usage costs across one or all agents: summaries, recent completions, or breakdowns by day/model/agent/channel/peer.";
    public override string Label => "Costs";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "summary";
        var scope = GetString(arguments, "scope") ?? "self";
        var period = GetString(arguments, "period") ?? "today";
        var (from, to) = ResolvePeriod(period);

        var resolution = ResolveScope(scope);
        if (resolution.Error is not null)
            return TextResult(resolution.Error);

        var tagged = await LoadEntriesAsync(resolution.Agents, from, to);

        return action switch
        {
            "summary" => SummaryFor(tagged, resolution, period),
            "recent" => RecentFor(tagged, resolution, GetInt(arguments, "count", 10)),
            "breakdown" => BreakdownFor(tagged, resolution, period, GetString(arguments, "group_by") ?? "day"),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    // ── scope resolution ────────────────────────────────────────────────────

    private readonly record struct ScopeResolution(
        IReadOnlyList<string> Agents,
        bool IsCrossAgent,
        string Label,
        string? Error);

    private ScopeResolution ResolveScope(string scope)
    {
        if (scope.Equals("self", StringComparison.OrdinalIgnoreCase))
            return new([callingAgent], IsCrossAgent: false, Label: callingAgent, Error: null);

        if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var names = ledgers.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(names, IsCrossAgent: true, Label: "all agents", Error: null);
        }

        // Treat as a specific agent name (case-insensitive lookup).
        var match = ledgers.Keys.FirstOrDefault(k =>
            k.Equals(scope, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var available = string.Join(", ", ledgers.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return new([], IsCrossAgent: false, Label: scope,
                Error: $"Unknown agent: {scope}. Available: {available}.");
        }

        return new([match], IsCrossAgent: false, Label: match, Error: null);
    }

    private async Task<IReadOnlyList<(string Agent, CostEntry Entry)>> LoadEntriesAsync(
        IReadOnlyList<string> agents, DateTimeOffset? from, DateTimeOffset? to)
    {
        var all = new List<(string Agent, CostEntry Entry)>();
        foreach (var agent in agents)
        {
            if (!ledgers.TryGetValue(agent, out var ledger))
                continue;
            var entries = await ledger.QueryAsync(from, to);
            foreach (var e in entries)
                all.Add((agent, e));
        }
        return all;
    }

    // ── summary ─────────────────────────────────────────────────────────────

    private static AgentToolResult SummaryFor(
        IReadOnlyList<(string Agent, CostEntry Entry)> tagged,
        ScopeResolution resolution,
        string period)
    {
        if (tagged.Count == 0)
        {
            return TextResult(resolution.IsCrossAgent
                ? $"No usage recorded for period '{period}' across {resolution.Agents.Count} agent(s)."
                : $"No usage recorded for period '{period}'.");
        }

        var sb = new StringBuilder();
        var label = PeriodLabel(period);
        var totalCost = tagged.Sum(t => t.Entry.CostTotal);

        // Header line — same shape regardless of scope, with the scope label inline when cross-agent.
        sb.AppendLine(resolution.IsCrossAgent
            ? $"**{label}** ({resolution.Label}): ${totalCost:F4}"
            : $"**{label}**: ${totalCost:F4}");

        // Per-agent table when aggregating across more than one agent.
        if (resolution.IsCrossAgent)
        {
            sb.AppendLine();
            sb.AppendLine("Per agent:");
            var perAgent = tagged
                .GroupBy(t => t.Agent, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(t => t.Entry.CostTotal));
            foreach (var g in perAgent)
                sb.AppendLine($"  {g.Key}: ${g.Sum(t => t.Entry.CostTotal):F4} ({g.Count()} completions)");
            sb.AppendLine();
        }

        var entries = tagged.Select(t => t.Entry).ToArray();
        sb.AppendLine($"Completions: {entries.Length}");
        sb.AppendLine($"Input tokens: {entries.Sum(e => e.InputTokens):N0}");
        sb.AppendLine($"Output tokens: {entries.Sum(e => e.OutputTokens):N0}");

        var totalCacheRead = entries.Sum(e => e.CacheReadTokens);
        var totalCacheWrite = entries.Sum(e => e.CacheWriteTokens);
        if (totalCacheRead > 0)
            sb.AppendLine($"Cache read tokens: {totalCacheRead:N0}");
        if (totalCacheWrite > 0)
            sb.AppendLine($"Cache write tokens: {totalCacheWrite:N0}");

        // Full cost split — only when any non-zero category cost exists, so the no-cache common case stays clean.
        var costInput = entries.Sum(e => e.CostInput);
        var costOutput = entries.Sum(e => e.CostOutput);
        var costCacheRead = entries.Sum(e => e.CostCacheRead);
        var costCacheWrite = entries.Sum(e => e.CostCacheWrite);
        if (costInput > 0 || costOutput > 0 || costCacheRead > 0 || costCacheWrite > 0)
        {
            sb.Append("Cost split: ");
            sb.Append($"input ${costInput:F4} · output ${costOutput:F4}");
            if (costCacheRead > 0) sb.Append($" · cache-read ${costCacheRead:F4}");
            if (costCacheWrite > 0) sb.Append($" · cache-write ${costCacheWrite:F4}");
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    // ── recent ──────────────────────────────────────────────────────────────

    private static AgentToolResult RecentFor(
        IReadOnlyList<(string Agent, CostEntry Entry)> tagged,
        ScopeResolution resolution,
        int count)
    {
        count = Math.Clamp(count, 1, 100);

        if (tagged.Count == 0)
            return TextResult("No recent usage entries.");

        // Interleave by timestamp across agents so 'last N' is meaningful cross-agent.
        var ordered = tagged
            .OrderBy(t => t.Entry.Timestamp)
            .TakeLast(count);

        var sb = new StringBuilder();
        foreach (var (agent, e) in ordered)
        {
            var local = e.Timestamp.ToLocalTime();
            sb.Append($"{local:yyyy-MM-dd HH:mm}");
            if (resolution.IsCrossAgent)
                sb.Append($" | {agent}");
            sb.Append($" | {e.Model} | {e.Channel} | ");
            sb.Append($"{e.InputTokens} in / {e.OutputTokens} out");
            if (e.CacheReadTokens > 0 || e.CacheWriteTokens > 0)
                sb.Append($" (cache r{e.CacheReadTokens}/w{e.CacheWriteTokens})");
            sb.AppendLine($" | ${e.CostTotal:F4}");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    // ── breakdown ───────────────────────────────────────────────────────────

    private static AgentToolResult BreakdownFor(
        IReadOnlyList<(string Agent, CostEntry Entry)> tagged,
        ScopeResolution resolution,
        string period,
        string groupBy)
    {
        if (tagged.Count == 0)
        {
            return TextResult(resolution.IsCrossAgent
                ? $"No usage recorded for period '{period}' across {resolution.Agents.Count} agent(s)."
                : $"No usage recorded for period '{period}'.");
        }

        IEnumerable<IGrouping<string, (string Agent, CostEntry Entry)>> groups = groupBy switch
        {
            "model"   => tagged.GroupBy(t => t.Entry.Model),
            "agent"   => tagged.GroupBy(t => t.Agent, StringComparer.OrdinalIgnoreCase),
            "channel" => tagged.GroupBy(t => t.Entry.Channel),
            "peer"    => tagged.GroupBy(t => t.Entry.Peer),
            _         => tagged.GroupBy(t => t.Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd")),
        };

        var sb = new StringBuilder();
        sb.AppendLine(resolution.IsCrossAgent
            ? $"**{PeriodLabel(period)}** ({resolution.Label}) by {groupBy}:"
            : $"**{PeriodLabel(period)}** by {groupBy}:");
        sb.AppendLine();

        // Cost-ordered for model/agent/channel/peer; chronological for day.
        var ordered = groupBy == "day"
            ? groups.OrderBy(g => g.Key, StringComparer.Ordinal)
            : groups.OrderByDescending(g => g.Sum(t => t.Entry.CostTotal));

        foreach (var g in ordered)
        {
            var cost = g.Sum(t => t.Entry.CostTotal);
            var n = g.Count();
            var input = g.Sum(t => t.Entry.InputTokens);
            var output = g.Sum(t => t.Entry.OutputTokens);
            sb.AppendLine($"**{g.Key}**: ${cost:F4} ({n} completions, {input:N0} in / {output:N0} out)");
        }

        var total = tagged.Sum(t => t.Entry.CostTotal);
        sb.AppendLine();
        sb.AppendLine($"**Total**: ${total:F4}");

        return TextResult(sb.ToString().TrimEnd());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (DateTimeOffset? from, DateTimeOffset? to) ResolvePeriod(string period)
    {
        var now = DateTimeOffset.Now;
        return period switch
        {
            "today" => (new DateTimeOffset(now.Date, now.Offset), null),
            "week" => (now.AddDays(-7), null),
            "month" => (now.AddDays(-30), null),
            "all" => (null, null),
            _ => (new DateTimeOffset(now.Date, now.Offset), null),
        };
    }

    private static string PeriodLabel(string period) => period switch
    {
        "today" => "Today",
        "week" => "Last 7 days",
        "month" => "Last 30 days",
        "all" => "All time",
        _ => period,
    };

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
}
