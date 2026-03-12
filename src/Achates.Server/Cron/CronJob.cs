using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server.Cron;

/// <summary>
/// When and how often a job should run.
/// </summary>
[JsonConverter(typeof(CronScheduleConverter))]
public abstract record CronSchedule
{
    /// <summary>One-shot: fire once at a specific time.</summary>
    public sealed record At(DateTimeOffset Time) : CronSchedule;

    /// <summary>Recurring: fire every N minutes.</summary>
    public sealed record Every(TimeSpan Interval) : CronSchedule;

    /// <summary>Cron expression with optional timezone.</summary>
    public sealed record Cron(string Expression, string? Timezone = null) : CronSchedule;
}

/// <summary>
/// Where to deliver the job's output.
/// </summary>
public sealed record CronDeliveryTarget
{
    public required string ChannelName { get; init; }
    public required string PeerId { get; init; }
}

/// <summary>
/// Mutable execution state tracked between runs.
/// </summary>
public sealed class CronJobState
{
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveErrors { get; set; }
}

/// <summary>
/// A scheduled job definition.
/// </summary>
public sealed class CronJob
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string AgentName { get; init; }
    public required CronSchedule Schedule { get; set; }
    public required string Message { get; set; }
    public required CronDeliveryTarget Delivery { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CronJobState State { get; init; } = new();
}

/// <summary>
/// JSON converter for the <see cref="CronSchedule"/> discriminated union.
/// Uses a "kind" field: "at", "every", "cron".
/// </summary>
public sealed class CronScheduleConverter : JsonConverter<CronSchedule>
{
    public override CronSchedule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var kind = root.GetProperty("kind").GetString()
            ?? throw new JsonException("CronSchedule missing 'kind' property.");

        return kind switch
        {
            "at" => new CronSchedule.At(
                root.GetProperty("time").GetDateTimeOffset()),
            "every" => new CronSchedule.Every(
                TimeSpan.FromMinutes(root.GetProperty("interval_minutes").GetDouble())),
            "cron" => new CronSchedule.Cron(
                root.GetProperty("expression").GetString()!,
                root.TryGetProperty("timezone", out var tz) ? tz.GetString() : null),
            _ => throw new JsonException($"Unknown CronSchedule kind: {kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, CronSchedule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case CronSchedule.At at:
                writer.WriteString("kind", "at");
                writer.WriteString("time", at.Time);
                break;
            case CronSchedule.Every every:
                writer.WriteString("kind", "every");
                writer.WriteNumber("interval_minutes", every.Interval.TotalMinutes);
                break;
            case CronSchedule.Cron cron:
                writer.WriteString("kind", "cron");
                writer.WriteString("expression", cron.Expression);
                if (cron.Timezone is not null)
                    writer.WriteString("timezone", cron.Timezone);
                break;
        }

        writer.WriteEndObject();
    }
}
