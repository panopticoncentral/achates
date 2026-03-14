using System.Text.Json;
using Achates.Server.Cron;

namespace Achates.Tests;

public sealed class CronScheduleConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // --- At schedule ---

    [Fact]
    public void At_schedule_round_trips()
    {
        var original = new CronSchedule.At(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize<CronSchedule>(original, Options);
        var deserialized = JsonSerializer.Deserialize<CronSchedule>(json, Options);

        var at = Assert.IsType<CronSchedule.At>(deserialized);
        Assert.Equal(original.Time, at.Time);
    }

    [Fact]
    public void At_schedule_serializes_kind()
    {
        var schedule = new CronSchedule.At(DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize<CronSchedule>(schedule, Options);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("at", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.TryGetProperty("time", out _));
    }

    // --- Every schedule ---

    [Fact]
    public void Every_schedule_round_trips()
    {
        var original = new CronSchedule.Every(TimeSpan.FromMinutes(30));

        var json = JsonSerializer.Serialize<CronSchedule>(original, Options);
        var deserialized = JsonSerializer.Deserialize<CronSchedule>(json, Options);

        var every = Assert.IsType<CronSchedule.Every>(deserialized);
        Assert.Equal(TimeSpan.FromMinutes(30), every.Interval);
    }

    [Fact]
    public void Every_schedule_serializes_interval_minutes()
    {
        var schedule = new CronSchedule.Every(TimeSpan.FromHours(2));

        var json = JsonSerializer.Serialize<CronSchedule>(schedule, Options);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("every", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(120, doc.RootElement.GetProperty("interval_minutes").GetDouble());
    }

    // --- Cron schedule ---

    [Fact]
    public void Cron_schedule_round_trips()
    {
        var original = new CronSchedule.Cron("0 9 * * 1-5", "America/New_York");

        var json = JsonSerializer.Serialize<CronSchedule>(original, Options);
        var deserialized = JsonSerializer.Deserialize<CronSchedule>(json, Options);

        var cron = Assert.IsType<CronSchedule.Cron>(deserialized);
        Assert.Equal("0 9 * * 1-5", cron.Expression);
        Assert.Equal("America/New_York", cron.Timezone);
    }

    [Fact]
    public void Cron_schedule_without_timezone_round_trips()
    {
        var original = new CronSchedule.Cron("*/5 * * * *");

        var json = JsonSerializer.Serialize<CronSchedule>(original, Options);
        var deserialized = JsonSerializer.Deserialize<CronSchedule>(json, Options);

        var cron = Assert.IsType<CronSchedule.Cron>(deserialized);
        Assert.Equal("*/5 * * * *", cron.Expression);
        Assert.Null(cron.Timezone);
    }

    [Fact]
    public void Cron_schedule_without_timezone_omits_it_in_json()
    {
        var schedule = new CronSchedule.Cron("0 * * * *");

        var json = JsonSerializer.Serialize<CronSchedule>(schedule, Options);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("timezone", out _));
    }

    // --- Error cases ---

    [Fact]
    public void Missing_kind_throws()
    {
        var json = """{"expression": "0 * * * *"}""";

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<CronSchedule>(json, Options));
    }

    [Fact]
    public void Unknown_kind_throws()
    {
        var json = """{"kind": "unknown"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CronSchedule>(json, Options));
    }
}
