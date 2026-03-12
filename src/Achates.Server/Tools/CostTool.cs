using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Queries the persistent cost ledger for usage summaries, recent entries, and breakdowns.
/// </summary>
internal sealed class CostTool(CostLedger ledger) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["summary", "recent", "breakdown"],
                "Action to perform: summary (totals), recent (last N entries), breakdown (grouped)."),
            ["period"] = StringEnum(["today", "week", "month", "all"],
                "Time period to query.", "today"),
            ["count"] = NumberSchema("Number of recent entries to return. Default 10. Only used with 'recent' action."),
            ["group_by"] = StringEnum(["day", "model"],
                "Grouping for breakdown action.", "day"),
        },
        required: ["action"]);

    public override string Name => "cost";
    public override string Description => "Query usage costs: summaries, recent completions, or breakdowns by day/model.";
    public override string Label => "Cost";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "summary";
        var period = GetString(arguments, "period") ?? "today";
        var (from, to) = ResolvePeriod(period);

        return action switch
        {
            "summary" => await SummaryAsync(from, to, period),
            "recent" => await RecentAsync(from, to, GetInt(arguments, "count", 10)),
            "breakdown" => await BreakdownAsync(from, to, period, GetString(arguments, "group_by") ?? "day"),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, string period)
    {
        var entries = await ledger.QueryAsync(from, to);

        if (entries.Count == 0)
            return TextResult($"No usage recorded for period '{period}'.");

        var totalCost = entries.Sum(e => e.CostTotal);
        var totalInput = entries.Sum(e => e.InputTokens);
        var totalOutput = entries.Sum(e => e.OutputTokens);
        var totalCacheRead = entries.Sum(e => e.CacheReadTokens);

        var sb = new StringBuilder();
        sb.AppendLine($"**{PeriodLabel(period)}**: ${totalCost:F4}");
        sb.AppendLine($"Completions: {entries.Count}");
        sb.AppendLine($"Input tokens: {totalInput:N0}");
        sb.AppendLine($"Output tokens: {totalOutput:N0}");
        if (totalCacheRead > 0)
            sb.AppendLine($"Cache read tokens: {totalCacheRead:N0}");

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> RecentAsync(DateTimeOffset? from, DateTimeOffset? to, int count)
    {
        var entries = await ledger.QueryAsync(from, to);
        count = Math.Clamp(count, 1, 100);

        if (entries.Count == 0)
            return TextResult("No recent usage entries.");

        var recent = entries.TakeLast(count);
        var sb = new StringBuilder();

        foreach (var e in recent)
        {
            var local = e.Timestamp.ToLocalTime();
            sb.AppendLine($"{local:yyyy-MM-dd HH:mm} | {e.Model} | {e.InputTokens} in / {e.OutputTokens} out | ${e.CostTotal:F4}");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> BreakdownAsync(
        DateTimeOffset? from, DateTimeOffset? to, string period, string groupBy)
    {
        var entries = await ledger.QueryAsync(from, to);

        if (entries.Count == 0)
            return TextResult($"No usage recorded for period '{period}'.");

        var groups = groupBy == "model"
            ? entries.GroupBy(e => e.Model)
            : entries.GroupBy(e => e.Timestamp.ToLocalTime().ToString("yyyy-MM-dd"));

        var sb = new StringBuilder();
        sb.AppendLine($"**{PeriodLabel(period)}** by {groupBy}:");
        sb.AppendLine();

        foreach (var g in groups.OrderBy(g => g.Key))
        {
            var cost = g.Sum(e => e.CostTotal);
            var count = g.Count();
            var input = g.Sum(e => e.InputTokens);
            var output = g.Sum(e => e.OutputTokens);
            sb.AppendLine($"**{g.Key}**: ${cost:F4} ({count} completions, {input:N0} in / {output:N0} out)");
        }

        var total = entries.Sum(e => e.CostTotal);
        sb.AppendLine();
        sb.AppendLine($"**Total**: ${total:F4}");

        return TextResult(sb.ToString().TrimEnd());
    }

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
