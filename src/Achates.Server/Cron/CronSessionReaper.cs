using Achates.Server.Mobile;

namespace Achates.Server.Cron;

/// <summary>
/// Prunes old cron-origin sessions so recurring jobs don't bloat the session list.
///
/// Only touches sessions whose <see cref="MobileSession.JobId"/> is set and whose
/// originating job is <see cref="CronJobKind.User"/>. Dreamtime sessions are left
/// alone so they remain auditable.
///
/// Rule: for each JobId, keep the N most-recent sessions (default 1). Additionally,
/// drop anything older than <see cref="CronConfig.MaxAgeDays"/> regardless of count.
/// </summary>
public sealed class CronSessionReaper(
    MobileSessionStore sessionStore,
    CronConfig? config,
    ILogger<CronSessionReaper> logger)
{
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DateTimeOffset> _lastSweepAt = [];

    /// <summary>
    /// Sweep cron-origin sessions for the given agents. Self-throttled to once
    /// per <see cref="MinSweepInterval"/> per agent. Pass <paramref name="force"/>
    /// to bypass the throttle (tests / manual triggers).
    /// </summary>
    public async Task SweepAsync(
        IEnumerable<(string AgentName, IReadOnlyList<CronJob> Jobs)> agents,
        CancellationToken ct = default,
        bool force = false)
    {
        var keepLast = config?.KeepLastPerJob ?? 1;
        var maxAge = config?.MaxAgeDays is { } days && days > 0
            ? TimeSpan.FromDays(days)
            : (TimeSpan?)TimeSpan.FromDays(30);

        // Nothing to do if both rules are disabled.
        if (keepLast <= 0 && maxAge is null) return;

        var now = DateTimeOffset.UtcNow;

        foreach (var (agentName, jobs) in agents)
        {
            if (ct.IsCancellationRequested) break;

            if (!force
                && _lastSweepAt.TryGetValue(agentName, out var last)
                && now - last < MinSweepInterval)
            {
                continue;
            }

            try
            {
                var pruned = await SweepAgentAsync(agentName, jobs, keepLast, maxAge, now, ct);
                _lastSweepAt[agentName] = now;

                if (pruned > 0)
                {
                    logger.LogInformation(
                        "cron-reaper: pruned {Count} old cron session(s) for agent '{Agent}'",
                        pruned, agentName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "cron-reaper: sweep failed for agent '{Agent}'", agentName);
            }
        }
    }

    private async Task<int> SweepAgentAsync(
        string agentName,
        IReadOnlyList<CronJob> jobs,
        int keepLast,
        TimeSpan? maxAge,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Build a set of User-kind job IDs. Dreamtime jobs are exempt.
        var userJobIds = new HashSet<string>(
            jobs.Where(j => j.Kind == CronJobKind.User).Select(j => j.Id));

        // List all sessions (paginated API — walk to the end).
        var all = new List<MobileSessionInfo>();
        DateTimeOffset? before = null;
        while (true)
        {
            var (page, hasMore) = await sessionStore.ListAsync(agentName, before, 100, ct);
            all.AddRange(page);
            if (!hasMore || page.Count == 0) break;
            before = page[^1].Updated;
        }

        var victims = new List<string>();

        // Absolute max-age ceiling across all cron-origin sessions.
        if (maxAge is { } age)
        {
            var cutoff = now - age;
            foreach (var s in all)
            {
                if (s.JobId is null) continue;
                if (!userJobIds.Contains(s.JobId)) continue;
                if (s.Updated < cutoff) victims.Add(s.Id);
            }
        }

        // Keep-N-per-job. Sessions are already sorted Updated desc by ListAsync.
        if (keepLast > 0)
        {
            var byJob = all
                .Where(s => s.JobId is not null && userJobIds.Contains(s.JobId))
                .GroupBy(s => s.JobId!);

            foreach (var group in byJob)
            {
                foreach (var s in group.Skip(keepLast))
                    victims.Add(s.Id);
            }
        }

        if (victims.Count == 0) return 0;

        var unique = new HashSet<string>(victims);
        foreach (var id in unique)
        {
            if (ct.IsCancellationRequested) break;
            await sessionStore.DeleteAsync(agentName, id, ct);
        }

        return unique.Count;
    }
}
