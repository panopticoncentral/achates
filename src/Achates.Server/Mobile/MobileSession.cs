using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

public sealed class MobileSession
{
    public required string Id { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// If this session was created by a scheduled cron job run, the job's ID.
    /// Null for user-initiated sessions. Used by the session reaper to prune
    /// old cron run sessions.
    /// </summary>
    public string? JobId { get; set; }

    public List<AgentMessage> Messages { get; set; } = [];
}

public sealed record MobileSessionInfo(
    string Id,
    string? Title,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    int MessageCount,
    string? Preview,
    string? JobId,
    string? CronTaskName = null);
