using Achates.Server.Cron;

namespace Achates.Tests;

public sealed class CronSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // --- At schedule ---

    [Fact]
    public void At_future_time_returns_that_time()
    {
        var futureTime = Now.AddHours(1);
        var schedule = new CronSchedule.At(futureTime);

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.Equal(futureTime, next);
    }

    [Fact]
    public void At_past_time_returns_null()
    {
        var pastTime = Now.AddHours(-1);
        var schedule = new CronSchedule.At(pastTime);

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.Null(next);
    }

    [Fact]
    public void At_exact_current_time_returns_null()
    {
        var schedule = new CronSchedule.At(Now);

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.Null(next);
    }

    // --- Every schedule ---

    [Fact]
    public void Every_adds_interval_to_now()
    {
        var interval = TimeSpan.FromMinutes(30);
        var schedule = new CronSchedule.Every(interval);

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.Equal(Now + interval, next);
    }

    [Fact]
    public void Every_one_minute()
    {
        var schedule = new CronSchedule.Every(TimeSpan.FromMinutes(1));

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.Equal(Now.AddMinutes(1), next);
    }

    // --- Cron expression schedule ---

    [Fact]
    public void Cron_five_field_expression()
    {
        // Every hour at minute 0
        var schedule = new CronSchedule.Cron("0 * * * *");

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.NotNull(next);
        Assert.Equal(0, next.Value.Minute);
        Assert.True(next > Now);
    }

    [Fact]
    public void Cron_six_field_expression_with_seconds()
    {
        // Every minute at second 30
        var schedule = new CronSchedule.Cron("30 * * * * *");

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.NotNull(next);
        Assert.Equal(30, next.Value.Second);
    }

    [Fact]
    public void Cron_with_timezone()
    {
        // Every day at midnight UTC
        var schedule = new CronSchedule.Cron("0 0 * * *", "UTC");

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.NotNull(next);
        Assert.Equal(0, next.Value.Hour);
        Assert.Equal(0, next.Value.Minute);
    }

    [Fact]
    public void Cron_invalid_expression_throws()
    {
        var schedule = new CronSchedule.Cron("not a cron");

        Assert.ThrowsAny<Exception>(() => CronScheduler.ComputeNextRun(schedule, Now));
    }

    [Fact]
    public void Cron_next_occurrence_is_always_in_the_future()
    {
        // Every 5 minutes
        var schedule = new CronSchedule.Cron("*/5 * * * *");

        var next = CronScheduler.ComputeNextRun(schedule, Now);

        Assert.NotNull(next);
        Assert.True(next > Now);
    }
}
