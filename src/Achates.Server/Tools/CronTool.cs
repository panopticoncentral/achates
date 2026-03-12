using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Cron;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Agent-facing tool for managing scheduled tasks (cron jobs).
/// Per-session: infers delivery target from current channel+peer.
/// </summary>
internal sealed class CronTool(
    CronStore store,
    string agentName,
    string channelName,
    string peerId,
    CronService cronService) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "add", "update", "remove", "run"],
                "Action to perform."),
            ["name"] = StringSchema("Job name. Required for 'add'."),
            ["message"] = StringSchema("The prompt/instruction the agent will execute on schedule. Required for 'add'."),
            ["schedule_kind"] = StringEnum(["at", "every", "cron"],
                "Schedule type. Required for 'add'."),
            ["schedule_at"] = StringSchema("ISO 8601 timestamp for one-shot schedule (e.g. '2026-03-15T10:00:00'). Required when schedule_kind is 'at'."),
            ["schedule_interval_minutes"] = NumberSchema("Interval in minutes for recurring schedule. Required when schedule_kind is 'every'."),
            ["schedule_cron"] = StringSchema("Cron expression (e.g. '0 9 * * *' for daily at 9am). Required when schedule_kind is 'cron'."),
            ["schedule_timezone"] = StringSchema("IANA timezone for cron/at schedules (e.g. 'America/New_York'). Defaults to local timezone."),
            ["channel"] = StringSchema("Delivery channel name. Defaults to current channel."),
            ["peer"] = StringSchema("Delivery peer ID. Defaults to current peer."),
            ["job_id"] = StringSchema("Job ID. Required for 'update', 'remove', 'run'."),
            ["enabled"] = BooleanSchema("Enable or disable a job. Used with 'update'."),
        },
        required: ["action"]);

    public override string Name => "cron";
    public override string Description => "Manage scheduled tasks: create recurring jobs, one-shot reminders, or cron-based schedules.";
    public override string Label => "Scheduled Tasks";
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
            "add" => await AddAsync(arguments, cancellationToken),
            "update" => await UpdateAsync(arguments, cancellationToken),
            "remove" => await RemoveAsync(arguments, cancellationToken),
            "run" => await RunAsync(arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListAsync(CancellationToken ct)
    {
        var jobs = await store.LoadAsync(ct);

        if (jobs.Count == 0)
            return TextResult("No scheduled jobs.");

        var sb = new StringBuilder();
        foreach (var job in jobs)
        {
            var status = job.Enabled ? "enabled" : "disabled";
            var schedule = FormatSchedule(job.Schedule);
            var nextRun = job.State.NextRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
            var lastStatus = job.State.LastStatus ?? "—";

            sb.AppendLine($"**{job.Name}** (`{job.Id}`)");
            sb.AppendLine($"  Schedule: {schedule} | Status: {status} | Next: {nextRun} | Last: {lastStatus}");
            sb.AppendLine($"  Message: {Truncate(job.Message, 80)}");
            sb.AppendLine($"  Deliver to: {job.Delivery.ChannelName}:{job.Delivery.PeerId}");
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> AddAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        var name = GetString(args, "name");
        var message = GetString(args, "message");
        var scheduleKind = GetString(args, "schedule_kind");

        if (string.IsNullOrWhiteSpace(name))
            return TextResult("Error: 'name' is required.");
        if (string.IsNullOrWhiteSpace(message))
            return TextResult("Error: 'message' is required.");
        if (string.IsNullOrWhiteSpace(scheduleKind))
            return TextResult("Error: 'schedule_kind' is required.");

        CronSchedule schedule;
        try
        {
            schedule = ParseSchedule(scheduleKind, args);
        }
        catch (Exception ex)
        {
            return TextResult($"Error parsing schedule: {ex.Message}");
        }

        var deliveryChannel = GetString(args, "channel") ?? channelName;
        var deliveryPeer = GetString(args, "peer") ?? peerId;

        var now = DateTimeOffset.UtcNow;
        var nextRun = CronScheduler.ComputeNextRun(schedule, now);

        var job = new CronJob
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            AgentName = agentName,
            Schedule = schedule,
            Message = message,
            Delivery = new CronDeliveryTarget
            {
                ChannelName = deliveryChannel,
                PeerId = deliveryPeer,
            },
            State = { NextRunAt = nextRun },
        };

        await store.AddAsync(job, ct);
        cronService.Poke();

        var nextRunStr = nextRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "none";
        return TextResult($"Created job **{name}** (`{job.Id}`)\nSchedule: {FormatSchedule(schedule)}\nNext run: {nextRunStr}");
    }

    private async Task<AgentToolResult> UpdateAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        var jobId = GetString(args, "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
            return TextResult("Error: 'job_id' is required.");

        var updated = await store.UpdateAsync(jobId, job =>
        {
            if (GetString(args, "name") is { Length: > 0 } newName)
                job.Name = newName;
            if (GetString(args, "message") is { Length: > 0 } newMessage)
                job.Message = newMessage;
            if (args.ContainsKey("enabled"))
                job.Enabled = GetBool(args, "enabled") ?? job.Enabled;

            if (GetString(args, "schedule_kind") is { Length: > 0 } kind)
            {
                job.Schedule = ParseSchedule(kind, args);
                job.State.NextRunAt = CronScheduler.ComputeNextRun(job.Schedule, DateTimeOffset.UtcNow);
            }
        }, ct);

        if (updated is null)
            return TextResult($"Job '{jobId}' not found.");

        cronService.Poke();
        return TextResult($"Updated job **{updated.Name}** (`{updated.Id}`).");
    }

    private async Task<AgentToolResult> RemoveAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        var jobId = GetString(args, "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
            return TextResult("Error: 'job_id' is required.");

        var removed = await store.RemoveAsync(jobId, ct);
        if (!removed)
            return TextResult($"Job '{jobId}' not found.");

        cronService.Poke();
        return TextResult($"Removed job `{jobId}`.");
    }

    private async Task<AgentToolResult> RunAsync(Dictionary<string, object?> args, CancellationToken ct)
    {
        var jobId = GetString(args, "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
            return TextResult("Error: 'job_id' is required.");

        var result = await cronService.RunJobAsync(agentName, jobId, ct);
        if (result is null)
            return TextResult($"Job '{jobId}' not found.");

        return TextResult($"Job executed. Result:\n\n{Truncate(result, 500)}");
    }

    private CronSchedule ParseSchedule(string kind, Dictionary<string, object?> args)
    {
        var timezone = GetString(args, "schedule_timezone");

        return kind switch
        {
            "at" => ParseAtSchedule(args, timezone),
            "every" => ParseEverySchedule(args),
            "cron" => ParseCronSchedule(args, timezone),
            _ => throw new ArgumentException($"Unknown schedule kind: {kind}"),
        };
    }

    private static CronSchedule ParseAtSchedule(Dictionary<string, object?> args, string? timezone)
    {
        var atStr = GetString(args, "schedule_at")
            ?? throw new ArgumentException("'schedule_at' is required for 'at' schedule.");

        var time = DateTimeOffset.Parse(atStr);

        // If the parsed time has no offset info and a timezone was provided, apply it
        if (timezone is not null && time.Offset == TimeSpan.Zero)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            time = TimeZoneInfo.ConvertTime(time, tz);
        }

        return new CronSchedule.At(time);
    }

    private static CronSchedule ParseEverySchedule(Dictionary<string, object?> args)
    {
        var minutes = GetDouble(args, "schedule_interval_minutes")
            ?? throw new ArgumentException("'schedule_interval_minutes' is required for 'every' schedule.");

        if (minutes <= 0)
            throw new ArgumentException("Interval must be positive.");

        return new CronSchedule.Every(TimeSpan.FromMinutes(minutes));
    }

    private static CronSchedule ParseCronSchedule(Dictionary<string, object?> args, string? timezone)
    {
        var expr = GetString(args, "schedule_cron")
            ?? throw new ArgumentException("'schedule_cron' is required for 'cron' schedule.");

        // Validate by parsing
        var fieldCount = expr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fieldCount >= 6 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard;
        Cronos.CronExpression.Parse(expr, format); // throws on invalid

        return new CronSchedule.Cron(expr, timezone);
    }

    private static string FormatSchedule(CronSchedule schedule) => schedule switch
    {
        CronSchedule.At at => $"once at {at.Time.ToLocalTime():yyyy-MM-dd HH:mm}",
        CronSchedule.Every every => $"every {FormatInterval(every.Interval)}",
        CronSchedule.Cron cron => $"cron `{cron.Expression}`" + (cron.Timezone is not null ? $" ({cron.Timezone})" : ""),
        _ => "unknown",
    };

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1) return $"{interval.TotalDays:F0} day(s)";
        if (interval.TotalHours >= 1) return $"{interval.TotalHours:F0} hour(s)";
        return $"{interval.TotalMinutes:F0} minute(s)";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static double? GetDouble(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDouble();
        if (val is double d) return d;
        return null;
    }

    private static bool? GetBool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
        }
        if (val is bool b) return b;
        return null;
    }
}
