using Cronos;

namespace Achates.Server.Cron;

/// <summary>
/// Computes the next run time for a schedule.
/// </summary>
public static class CronScheduler
{
    /// <summary>
    /// Compute the next run time, or null if the schedule is exhausted.
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(CronSchedule schedule, DateTimeOffset now)
    {
        return schedule switch
        {
            CronSchedule.At at => at.Time > now ? at.Time : null,
            CronSchedule.Every every => now + every.Interval,
            CronSchedule.Cron cron => ComputeNextCron(cron, now),
            _ => null,
        };
    }

    private static DateTimeOffset? ComputeNextCron(CronSchedule.Cron schedule, DateTimeOffset now)
    {
        // Detect 6-field (with seconds) vs 5-field cron expressions
        var fieldCount = schedule.Expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fieldCount >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        var expression = CronExpression.Parse(schedule.Expression, format);

        var tz = schedule.Timezone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone)
            : TimeZoneInfo.Local;

        var next = expression.GetNextOccurrence(now.UtcDateTime, tz);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
    }
}
