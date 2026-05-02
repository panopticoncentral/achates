using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Graph;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and creates Outlook calendar events via Microsoft Graph API.
/// </summary>
internal sealed class CalendarTool(IReadOnlyDictionary<string, GraphClient> graphClients) : AgentTool
{
    private readonly JsonElement _schema = ObjectSchema(
        BuildSchemaProperties(graphClients),
        required: ["action"]);

    public override string Name => "calendar";
    public override string Description => "View Outlook calendar (upcoming events, event details, availability) or create a new event.";
    public override string Label => "Calendar";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "upcoming";
        var graph = ResolveClient(graphClients, arguments);

        return action switch
        {
            "upcoming" => await UpcomingAsync(graph, arguments, cancellationToken),
            "read" => await ReadAsync(graph, arguments, cancellationToken),
            "availability" => await AvailabilityAsync(graph, arguments, cancellationToken),
            "create" => await CreateAsync(graph, arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> UpcomingAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var days = GetInt(arguments, "days", 7);
        var count = GetInt(arguments, "count", 20);
        days = Math.Clamp(days, 1, 90);
        count = Math.Clamp(count, 1, 50);

        var now = DateTimeOffset.UtcNow;
        var end = now.AddDays(days);

        var path = $"calendarView" +
            $"?startDateTime={now:yyyy-MM-ddTHH:mm:ssZ}" +
            $"&endDateTime={end:yyyy-MM-ddTHH:mm:ssZ}" +
            $"&$top={count}" +
            "&$select=id,subject,start,end,location,organizer,isAllDay,isCancelled,showAs" +
            "&$orderby=start/dateTime";

        var result = await graph.GetAsync<GraphCollection<GraphEvent>>(path, cancellationToken);
        return FormatEventList(result.Value, days);
    }

    private async Task<AgentToolResult> ReadAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var eventId = GetString(arguments, "event_id");
        if (string.IsNullOrWhiteSpace(eventId))
            return TextResult("event_id is required for 'read'.");

        var path = $"events/{Uri.EscapeDataString(eventId)}" +
            "?$select=id,subject,start,end,location,organizer,attendees,body,isAllDay,isCancelled,showAs,recurrence";

        var evt = await graph.GetAsync<GraphEvent>(path, cancellationToken);
        return FormatFullEvent(evt);
    }

    private async Task<AgentToolResult> AvailabilityAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var start = GetString(arguments, "start");
        var end = GetString(arguments, "end");

        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
            return TextResult("start and end are required for 'availability'.");

        var userEmail = await graph.GetUserEmailAsync(cancellationToken);
        var body = new
        {
            schedules = new[] { userEmail },
            startTime = new { dateTime = start, timeZone = "UTC" },
            endTime = new { dateTime = end, timeZone = "UTC" },
        };

        var result = await graph.PostAsync<GraphScheduleResponse>(
            "calendar/getSchedule", body, cancellationToken);

        return FormatAvailability(result, start, end);
    }

    private async Task<AgentToolResult> CreateAsync(
        GraphClient graph, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var subject = GetString(arguments, "subject");
        var start = GetString(arguments, "start");
        var end = GetString(arguments, "end");

        if (string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(start) ||
            string.IsNullOrWhiteSpace(end))
        {
            return TextResult("subject, start, and end are required for 'create'.");
        }

        var timeZone = GetString(arguments, "time_zone");
        if (string.IsNullOrWhiteSpace(timeZone)) timeZone = "UTC";

        var isAllDay = GetBool(arguments, "is_all_day", false);
        var location = GetString(arguments, "location");
        var bodyText = GetString(arguments, "body");

        var requestBody = new Dictionary<string, object>
        {
            ["subject"] = subject,
            ["start"] = new { dateTime = start, timeZone },
            ["end"] = new { dateTime = end, timeZone },
            ["isAllDay"] = isAllDay,
        };

        if (!string.IsNullOrWhiteSpace(location))
            requestBody["location"] = new { displayName = location };

        if (!string.IsNullOrWhiteSpace(bodyText))
            requestBody["body"] = new { contentType = "text", content = bodyText };

        GraphEvent created;
        try
        {
            created = await graph.PostAsync<GraphEvent>("events", requestBody, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"Failed to create event: {ex.Message}");
        }

        var startDt = ParseGraphDateTime(created.Start);
        var when = isAllDay
            ? startDt?.ToString("dddd, MMMM d, yyyy") + " (all day)"
            : startDt?.ToString("dddd, MMMM d, yyyy h:mm tt");

        return TextResult($"Created event \"{created.Subject}\" on {when} (id: `{created.Id}`).");
    }

    private static AgentToolResult FormatEventList(List<GraphEvent> events, int days)
    {
        if (events.Count == 0)
            return TextResult($"No events in the next {days} day(s).");

        var sb = new StringBuilder();
        sb.AppendLine($"**Upcoming events** (next {days} days, {events.Count} events):");
        sb.AppendLine();

        string? currentDate = null;
        foreach (var evt in events)
        {
            var startDt = ParseGraphDateTime(evt.Start);
            var endDt = ParseGraphDateTime(evt.End);
            var dateStr = startDt?.ToString("dddd, MMMM d");

            if (dateStr != currentDate)
            {
                if (currentDate is not null) sb.AppendLine();
                sb.AppendLine($"### {dateStr}");
                currentDate = dateStr;
            }

            var cancelled = evt.IsCancelled ? " ~~CANCELLED~~" : "";
            if (evt.IsAllDay)
            {
                sb.AppendLine($"- **{evt.Subject}** (all day){cancelled}");
            }
            else
            {
                var timeRange = $"{startDt?.ToString("h:mm tt")} - {endDt?.ToString("h:mm tt")}";
                sb.AppendLine($"- **{evt.Subject}** {timeRange}{cancelled}");
            }

            if (!string.IsNullOrWhiteSpace(evt.Location?.DisplayName))
                sb.AppendLine($"  Location: {evt.Location.DisplayName}");

            sb.AppendLine($"  ID: `{evt.Id}`");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static AgentToolResult FormatFullEvent(GraphEvent evt)
    {
        var sb = new StringBuilder();
        var cancelled = evt.IsCancelled ? " (CANCELLED)" : "";
        sb.AppendLine($"**{evt.Subject}**{cancelled}");

        var startDt = ParseGraphDateTime(evt.Start);
        var endDt = ParseGraphDateTime(evt.End);

        if (evt.IsAllDay)
        {
            sb.AppendLine($"All day: {startDt?.ToString("dddd, MMMM d, yyyy")}");
        }
        else
        {
            sb.AppendLine($"Start: {startDt?.ToString("dddd, MMMM d, yyyy h:mm tt")}");
            sb.AppendLine($"End: {endDt?.ToString("dddd, MMMM d, yyyy h:mm tt")}");
        }

        if (!string.IsNullOrWhiteSpace(evt.Location?.DisplayName))
            sb.AppendLine($"Location: {evt.Location.DisplayName}");

        if (evt.Organizer?.EmailAddress is { } organizer)
            sb.AppendLine($"Organizer: {organizer}");

        if (evt.ShowAs is not null)
            sb.AppendLine($"Show as: {evt.ShowAs}");

        if (evt.Recurrence?.Pattern is { } pattern)
            sb.AppendLine($"Recurrence: {pattern.Type} (every {pattern.Interval})");

        if (evt.Attendees is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("**Attendees:**");
            foreach (var att in evt.Attendees)
            {
                var response = att.Status?.Response ?? "none";
                var type = att.Type == "required" ? "" : $" ({att.Type})";
                sb.AppendLine($"- {att.EmailAddress}{type} — {response}");
            }
        }

        var body = evt.Body?.Content?.Trim();
        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine();
            sb.AppendLine("**Description:**");
            sb.AppendLine(body);
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static AgentToolResult FormatAvailability(GraphScheduleResponse response,
        string start, string end)
    {
        var items = response.Value?.FirstOrDefault()?.ScheduleItems;
        if (items is null or { Count: 0 })
            return TextResult($"No busy slots between {start} and {end}. Completely free.");

        var sb = new StringBuilder();
        sb.AppendLine($"**Schedule** ({start} to {end}):");
        sb.AppendLine();

        foreach (var entry in items)
        {
            var entryStart = ParseGraphDateTime(entry.Start);
            var entryEnd = ParseGraphDateTime(entry.End);
            var status = entry.Status ?? "busy";
            var subject = entry.Subject is not null ? $" — {entry.Subject}" : "";
            sb.AppendLine($"- [{status}] {entryStart?.ToString("g")} - {entryEnd?.ToString("g")}{subject}");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static DateTime? ParseGraphDateTime(GraphDateTimeZone? dt)
    {
        if (dt?.DateTime is null) return null;
        if (DateTime.TryParse(dt.DateTime, out var parsed))
        {
            // Graph returns UTC for calendarView, convert to local
            if (dt.TimeZone == "UTC")
                return TimeZoneInfo.ConvertTimeFromUtc(parsed, TimeZoneInfo.Local);
            return parsed;
        }
        return null;
    }

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

    private static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return defaultValue;
        if (val is JsonElement je)
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue,
            };
        return val is bool b ? b : defaultValue;
    }

    private static Dictionary<string, JsonElement> BuildSchemaProperties(
        IReadOnlyDictionary<string, GraphClient> clients)
    {
        var props = new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["upcoming", "read", "availability", "create"], "Action to perform.", "upcoming"),
            ["event_id"] = StringSchema("Event ID. Required for 'read'."),
            ["days"] = NumberSchema("Number of days to look ahead for 'upcoming'. Default 7."),
            ["count"] = NumberSchema("Max events to return for 'upcoming'. Default 20."),
            ["start"] = StringSchema("Start datetime (ISO 8601, no offset, e.g. '2026-05-02T15:00:00'). Required for 'availability' and 'create'."),
            ["end"] = StringSchema("End datetime (ISO 8601, no offset). Required for 'availability' and 'create'."),
            ["subject"] = StringSchema("Event title. Required for 'create'."),
            ["time_zone"] = StringSchema("IANA time zone for start/end (e.g. 'America/Los_Angeles'). Defaults to UTC. Used by 'create'."),
            ["location"] = StringSchema("Location display name. Optional for 'create'."),
            ["body"] = StringSchema("Event description / notes (plain text). Optional for 'create'."),
            ["is_all_day"] = BooleanSchema("If true, treat start/end as all-day (must be midnight in the supplied time zone). Default false. Used by 'create'."),
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
