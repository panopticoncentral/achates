using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Withings;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Queries health data from Withings (weight, blood pressure, sleep, activity).
/// </summary>
internal sealed class HealthTool(WithingsClient withings) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(
                ["weight", "blood_pressure", "sleep", "activity", "authorize"],
                "Action to perform."),
            ["days"] = NumberSchema("Number of days to look back. Default 7."),
        },
        required: ["action"]);

    // Withings measure type codes
    private const int TypeWeight = 1;
    private const int TypeHeight = 4;
    private const int TypeFatFreeMass = 5;
    private const int TypeFatRatio = 6;
    private const int TypeFatMass = 8;
    private const int TypeDiastolicBP = 9;
    private const int TypeSystolicBP = 10;
    private const int TypeHeartPulse = 11;
    private const int TypeMuscleMass = 76;
    private const int TypeBoneMass = 88;

    private static readonly Dictionary<int, (string Name, string Unit)> MeasureTypes = new()
    {
        [TypeWeight] = ("Weight", "kg"),
        [TypeHeight] = ("Height", "m"),
        [TypeFatFreeMass] = ("Fat-free mass", "kg"),
        [TypeFatRatio] = ("Fat ratio", "%"),
        [TypeFatMass] = ("Fat mass", "kg"),
        [TypeDiastolicBP] = ("Diastolic BP", "mmHg"),
        [TypeSystolicBP] = ("Systolic BP", "mmHg"),
        [TypeHeartPulse] = ("Heart rate", "bpm"),
        [TypeMuscleMass] = ("Muscle mass", "kg"),
        [TypeBoneMass] = ("Bone mass", "kg"),
    };

    public override string Name => "health";
    public override string Description => "Query health data from Withings (weight, blood pressure, sleep, activity).";
    public override string Label => "Health";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action");
        var days = GetInt(arguments, "days", 7);

        return action switch
        {
            "authorize" => HandleAuthorize(),
            _ when !withings.IsAuthorized => HandleAuthorize(),
            "weight" => await GetWeightAsync(days, cancellationToken),
            "blood_pressure" => await GetBloodPressureAsync(days, cancellationToken),
            "sleep" => await GetSleepAsync(days, cancellationToken),
            "activity" => await GetActivityAsync(days, cancellationToken),
            _ => TextResult($"Unknown action '{action}'. Use: weight, blood_pressure, sleep, activity, authorize."),
        };
    }

    private AgentToolResult HandleAuthorize()
    {
        if (withings.IsAuthorized)
            return TextResult("Already authorized with Withings.");

        var url = withings.GetAuthorizationUrl();
        return TextResult($"Withings authorization required. Ask the user to visit this URL to connect their account:\n\n{url}");
    }

    private async Task<AgentToolResult> GetWeightAsync(int days, CancellationToken ct)
    {
        var (startDate, endDate) = GetDateRange(days);
        var body = await withings.ApiAsync("measure", new Dictionary<string, string>
        {
            ["action"] = "getmeas",
            ["meastypes"] = $"{TypeWeight},{TypeFatRatio},{TypeFatMass},{TypeFatFreeMass},{TypeMuscleMass},{TypeBoneMass}",
            ["category"] = "1", // real measures only (not objectives)
            ["startdate"] = startDate.ToString(),
            ["enddate"] = endDate.ToString(),
        }, ct);

        if (!body.TryGetProperty("measuregrps", out var groups) || groups.GetArrayLength() == 0)
            return TextResult($"No weight measurements in the last {days} days.");

        var sb = new StringBuilder();
        sb.AppendLine($"Weight measurements (last {days} days):");
        sb.AppendLine();

        foreach (var group in groups.EnumerateArray())
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(group.GetProperty("date").GetInt64());
            var localDate = TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local);
            sb.Append($"**{localDate:MMM d, yyyy h:mm tt}**");

            if (!group.TryGetProperty("measures", out var measures))
                continue;

            var parts = new List<string>();
            foreach (var m in measures.EnumerateArray())
            {
                var type = m.GetProperty("type").GetInt32();
                var value = m.GetProperty("value").GetInt64();
                var unit = m.GetProperty("unit").GetInt32();
                var realValue = value * Math.Pow(10, unit);

                if (MeasureTypes.TryGetValue(type, out var info))
                    parts.Add($"{info.Name}: {realValue:F1} {info.Unit}");
            }

            if (parts.Count > 0)
            {
                sb.AppendLine();
                foreach (var part in parts)
                    sb.AppendLine($"  {part}");
            }
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> GetBloodPressureAsync(int days, CancellationToken ct)
    {
        var (startDate, endDate) = GetDateRange(days);
        var body = await withings.ApiAsync("measure", new Dictionary<string, string>
        {
            ["action"] = "getmeas",
            ["meastypes"] = $"{TypeSystolicBP},{TypeDiastolicBP},{TypeHeartPulse}",
            ["category"] = "1",
            ["startdate"] = startDate.ToString(),
            ["enddate"] = endDate.ToString(),
        }, ct);

        if (!body.TryGetProperty("measuregrps", out var groups) || groups.GetArrayLength() == 0)
            return TextResult($"No blood pressure measurements in the last {days} days.");

        var sb = new StringBuilder();
        sb.AppendLine($"Blood pressure (last {days} days):");
        sb.AppendLine();

        foreach (var group in groups.EnumerateArray())
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(group.GetProperty("date").GetInt64());
            var localDate = TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local);

            if (!group.TryGetProperty("measures", out var measures))
                continue;

            int? systolic = null, diastolic = null, pulse = null;
            foreach (var m in measures.EnumerateArray())
            {
                var type = m.GetProperty("type").GetInt32();
                var value = m.GetProperty("value").GetInt64();
                var unit = m.GetProperty("unit").GetInt32();
                var realValue = (int)(value * Math.Pow(10, unit));

                switch (type)
                {
                    case TypeSystolicBP: systolic = realValue; break;
                    case TypeDiastolicBP: diastolic = realValue; break;
                    case TypeHeartPulse: pulse = realValue; break;
                }
            }

            sb.Append($"**{localDate:MMM d, yyyy h:mm tt}**  ");
            if (systolic is not null && diastolic is not null)
                sb.Append($"{systolic}/{diastolic} mmHg");
            if (pulse is not null)
                sb.Append($"  HR: {pulse} bpm");
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> GetSleepAsync(int days, CancellationToken ct)
    {
        var (startDate, _) = GetDateRange(days);

        var body = await withings.ApiAsync("v2/sleep", new Dictionary<string, string>
        {
            ["action"] = "get",
            ["startdate"] = startDate.ToString(),
            ["enddate"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        }, ct);

        if (!body.TryGetProperty("series", out var series) || series.GetArrayLength() == 0)
            return TextResult($"No sleep data in the last {days} days.");

        return FormatSleep(series, days);
    }

    private static AgentToolResult FormatSleep(JsonElement series, int days)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sleep data (last {days} days):");
        sb.AppendLine();

        foreach (var segment in series.EnumerateArray())
        {
            var startTs = segment.TryGetProperty("startdate", out var s) ? s.GetInt64() : 0;
            var endTs = segment.TryGetProperty("enddate", out var e) ? e.GetInt64() : 0;
            var state = segment.TryGetProperty("state", out var st) ? st.GetInt32() : -1;

            var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(startTs), TimeZoneInfo.Local);
            var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(endTs), TimeZoneInfo.Local);
            var stateName = state switch
            {
                0 => "Awake",
                1 => "Light sleep",
                2 => "Deep sleep",
                3 => "REM",
                _ => $"State {state}",
            };
            var duration = TimeSpan.FromSeconds(endTs - startTs);

            sb.AppendLine($"  {start:MMM d h:mm tt} – {end:h:mm tt}  {stateName} ({FormatDuration(duration.TotalSeconds)})");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private async Task<AgentToolResult> GetActivityAsync(int days, CancellationToken ct)
    {
        var (startDate, _) = GetDateRange(days);
        var startYmd = DateTimeOffset.FromUnixTimeSeconds(startDate).ToString("yyyy-MM-dd");
        var endYmd = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        var body = await withings.ApiAsync("v2/measure", new Dictionary<string, string>
        {
            ["action"] = "getactivity",
            ["startdateymd"] = startYmd,
            ["enddateymd"] = endYmd,
            ["data_fields"] = "steps,distance,elevation,soft,moderate,intense,active,calories,totalcalories,hr_average,hr_min,hr_max,hr_zone_0,hr_zone_1,hr_zone_2,hr_zone_3",
        }, ct);

        if (!body.TryGetProperty("activities", out var activities) || activities.GetArrayLength() == 0)
            return TextResult($"No activity data in the last {days} days.");

        var sb = new StringBuilder();
        sb.AppendLine($"Activity (last {days} days):");
        sb.AppendLine();

        foreach (var day in activities.EnumerateArray())
        {
            var date = day.TryGetProperty("date", out var d) ? d.GetString() : null;
            sb.Append($"**{date}**");

            var parts = new List<string>();

            if (TryGetDouble(day, "steps", out var steps) && steps > 0)
                parts.Add($"Steps: {steps:N0}");
            if (TryGetDouble(day, "distance", out var dist) && dist > 0)
                parts.Add($"Distance: {dist:N0}m");
            if (TryGetDouble(day, "elevation", out var elev) && elev > 0)
                parts.Add($"Elevation: {elev:N0}m");
            if (TryGetDouble(day, "calories", out var cal) && cal > 0)
                parts.Add($"Active cal: {cal:N0}");
            if (TryGetDouble(day, "totalcalories", out var totalCal) && totalCal > 0)
                parts.Add($"Total cal: {totalCal:N0}");
            if (TryGetSeconds(day, "soft", out var soft) && soft > 0)
                parts.Add($"Light: {FormatDuration(soft)}");
            if (TryGetSeconds(day, "moderate", out var moderate) && moderate > 0)
                parts.Add($"Moderate: {FormatDuration(moderate)}");
            if (TryGetSeconds(day, "intense", out var intense) && intense > 0)
                parts.Add($"Intense: {FormatDuration(intense)}");
            if (TryGetDouble(day, "hr_average", out var hr) && hr > 0)
                parts.Add($"Avg HR: {hr:F0} bpm");

            if (parts.Count > 0)
            {
                sb.AppendLine();
                foreach (var part in parts)
                    sb.AppendLine($"  {part}");
            }
            sb.AppendLine();
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static (long Start, long End) GetDateRange(int days)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        return (start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m"
            : $"{ts.Minutes}m";
    }

    private static bool TryGetDouble(JsonElement el, string prop, out double value)
    {
        value = 0;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) { value = p.GetDouble(); return true; }
        return false;
    }

    private static bool TryGetSeconds(JsonElement el, string prop, out double value) =>
        TryGetDouble(el, prop, out value);

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
