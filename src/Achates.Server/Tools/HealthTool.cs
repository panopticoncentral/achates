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
                ["weight", "blood_pressure", "sleep", "activity", "workouts", "authorize"],
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

    private static readonly Dictionary<int, string> WorkoutCategories = new()
    {
        [1] = "Walk",
        [2] = "Run",
        [6] = "Cycle",
        [7] = "Swim",
        [8] = "Surf",
        [10] = "Yoga",
        [16] = "Pilates",
        [18] = "Weights",
        [19] = "Elliptical",
        [20] = "Rowing",
        [21] = "Zumba",
        [28] = "Boxing",
        [36] = "Hiking",
        [187] = "Climbing",
        [192] = "Snowboarding",
        [272] = "HIIT",
    };

    public override string Name => "health";
    public override string Description => "Query health data from Withings (weight, blood pressure, sleep, activity, workouts).";
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
            "workouts" => await GetWorkoutsAsync(days, cancellationToken),
            _ => TextResult($"Unknown action '{action}'. Use: weight, blood_pressure, sleep, activity, workouts, authorize."),
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
        var (startYmd, endYmd) = GetDateRangeYmd(days);

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

    private async Task<AgentToolResult> GetWorkoutsAsync(int days, CancellationToken ct)
    {
        var (startYmd, endYmd) = GetDateRangeYmd(days);
        var body = await withings.ApiAsync("v2/measure", new Dictionary<string, string>
        {
            ["action"] = "getworkouts",
            ["startdateymd"] = startYmd,
            ["enddateymd"] = endYmd,
            ["data_fields"] = "calories,distance,elevation,hr_average,hr_min,hr_max,hr_zone_0,hr_zone_1,hr_zone_2,hr_zone_3,steps",
        }, ct);

        if (!body.TryGetProperty("series", out var series) || series.GetArrayLength() == 0)
            return TextResult($"No workouts in the last {days} days.");

        var sb = new StringBuilder();
        sb.AppendLine($"Workouts (last {days} days):");
        sb.AppendLine();

        foreach (var workout in series.EnumerateArray())
        {
            var category = workout.TryGetProperty("category", out var c) ? c.GetInt32() : 0;
            var startTs = workout.TryGetProperty("startdate", out var s) ? s.GetInt64() : 0;
            var endTs = workout.TryGetProperty("enddate", out var e) ? e.GetInt64() : 0;
            var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(startTs), TimeZoneInfo.Local);
            var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(endTs), TimeZoneInfo.Local);
            var duration = TimeSpan.FromSeconds(endTs - startTs);
            var name = WorkoutCategories.TryGetValue(category, out var n) ? n : $"Activity {category}";

            sb.AppendLine($"**{start:MMM d, yyyy}**  {name} {start:h:mm tt} – {end:h:mm tt} ({FormatDuration(duration.TotalSeconds)})");

            if (!workout.TryGetProperty("data", out var data))
            {
                sb.AppendLine();
                continue;
            }

            var topLine = new List<string>();
            if (TryGetDouble(data, "distance", out var dist) && dist > 0)
                topLine.Add($"Distance: {dist:N0}m");
            if (TryGetDouble(data, "calories", out var cal) && cal > 0)
                topLine.Add($"Calories: {cal:N0}");
            if (topLine.Count > 0)
                sb.AppendLine($"  {string.Join("  ", topLine)}");

            if (TryGetDouble(data, "hr_average", out var hr) && hr > 0)
            {
                var hrLine = $"  Avg HR: {hr:F0} bpm";
                if (TryGetDouble(data, "hr_min", out var hrMin)
                    && TryGetDouble(data, "hr_max", out var hrMax)
                    && hrMin > 0 && hrMax > 0)
                    hrLine += $" ({hrMin:F0}–{hrMax:F0})";
                sb.AppendLine(hrLine);
            }

            TryGetSeconds(data, "hr_zone_0", out var z0);
            TryGetSeconds(data, "hr_zone_1", out var z1);
            TryGetSeconds(data, "hr_zone_2", out var z2);
            TryGetSeconds(data, "hr_zone_3", out var z3);
            if (z0 + z1 + z2 + z3 > 0)
            {
                // Withings hr_zone numbering: 0=light, 1=moderate, 2=intense, 3=peak.
                // Verify against a real workout response — if the zones look swapped,
                // adjust the labels here.
                var zones = new List<string>();
                if (z0 > 0) zones.Add($"light {FormatDuration(z0)}");
                if (z1 > 0) zones.Add($"moderate {FormatDuration(z1)}");
                if (z2 > 0) zones.Add($"intense {FormatDuration(z2)}");
                if (z3 > 0) zones.Add($"peak {FormatDuration(z3)}");
                sb.AppendLine($"  HR zones: {string.Join(", ", zones)}");
            }

            var extras = new List<string>();
            if (TryGetDouble(data, "elevation", out var elev) && elev > 0)
                extras.Add($"Elevation: {elev:N0}m");
            if (TryGetDouble(data, "steps", out var steps) && steps > 0)
                extras.Add($"Steps: {steps:N0}");
            if (extras.Count > 0)
                sb.AppendLine($"  {string.Join("  ", extras)}");

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

    private static (string StartYmd, string EndYmd) GetDateRangeYmd(int days)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-days);
        return (start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
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
