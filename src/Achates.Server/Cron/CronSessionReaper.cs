using Achates.Server.Mobile;

namespace Achates.Server.Cron;

/// <summary>
/// Prunes old cron-origin sessions so recurring jobs don't bloat the session list.
///
/// A session is treated as cron-origin if its <see cref="MobileSession.JobId"/> is
/// set, or — when the stamp was lost (e.g. an old chat-resave path) — if its first
/// message carries the <c>[Scheduled task: &lt;name&gt;]</c> fingerprint. Detection
/// therefore does not depend on the originating job still existing.
///
/// Rules:
/// - User-kind: for each job (by JobId, or task name when unstamped) keep the N
///   most-recent sessions (default 1), and drop anything older than
///   <see cref="CronConfig.MaxAgeDays"/>.
/// - Dreamtime-kind: keep the full nightly history (no keep-N) for auditability,
///   bounded only by the <see cref="CronConfig.MaxAgeDays"/> max-age ceiling.
/// - Chat-origin (<see cref="SessionSource.Chat"/>): inter-agent chat sessions
///   recorded for the target agent. Kept with no keep-N (so nightly dreamtime
///   has time to review them), bounded only by the max-age ceiling.
///
/// Note: the reaper only runs for agents in the cron loop (those with a
/// cron/dreamtime CronStore). An agent with no dreamtime won't review or reap
/// its chat sessions — acceptable, since the feature exists for dreamtime review.
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
            : (TimeSpan?)TimeSpan.FromDays(7);

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
        var jobsById = jobs.ToDictionary(j => j.Id);

        // Names that identify a dreamtime-origin session when the JobId is gone.
        // Dreamtime jobs are always created with Name "Dreamtime"; include the
        // literal as a defensive fallback.
        var dreamtimeNames = new HashSet<string>(StringComparer.Ordinal) { "Dreamtime" };
        foreach (var j in jobs.Where(j => j.Kind == CronJobKind.Dreamtime))
            dreamtimeNames.Add(j.Name);

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

        // Classify each session as cron-origin via JobId (preferred) or the
        // [Scheduled task: <name>] fingerprint (recovers sessions whose JobId
        // stamp was lost). Kind drives the retention policy below.
        var cron = new List<(MobileSessionInfo Session, CronJobKind Kind, string GroupKey)>();
        foreach (var s in all)
        {
            if (s.JobId is { } jobId)
            {
                var kind = jobsById.TryGetValue(jobId, out var job)
                    ? job.Kind
                    : (IsDreamtime(s.CronTaskName, dreamtimeNames) ? CronJobKind.Dreamtime : CronJobKind.User);
                cron.Add((s, kind, jobId));
            }
            else if (s.CronTaskName is { } taskName)
            {
                var kind = IsDreamtime(taskName, dreamtimeNames) ? CronJobKind.Dreamtime : CronJobKind.User;
                cron.Add((s, kind, "name:" + taskName));
            }
        }

        var victims = new List<string>();

        // Max-age ceiling — applies to every cron-origin session, both kinds,
        // plus chat-origin sessions (no keep-N, like dreamtime).
        if (maxAge is { } age)
        {
            var cutoff = now - age;
            foreach (var (s, _, _) in cron)
                if (s.Updated < cutoff) victims.Add(s.Id);
            foreach (var s in all)
                if (s.Source == SessionSource.Chat && s.Updated < cutoff)
                    victims.Add(s.Id);
        }

        // Keep-N-per-job — User-kind only. Dreamtime keeps its nightly history
        // (bounded only by max-age) for auditability. Sessions are already
        // sorted Updated desc by ListAsync.
        if (keepLast > 0)
        {
            var byJob = cron
                .Where(c => c.Kind == CronJobKind.User)
                .GroupBy(c => c.GroupKey);

            foreach (var group in byJob)
            {
                foreach (var c in group.Skip(keepLast))
                    victims.Add(c.Session.Id);
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

    private static bool IsDreamtime(string? taskName, HashSet<string> dreamtimeNames)
        => taskName is not null && dreamtimeNames.Contains(taskName);
}
