using Achates.Agent.Messages;

namespace Achates.Server.Mobile;

/// <summary>
/// Pure per-session unread logic. A session participates in unread tracking
/// unless it is an inter-agent chat session or a dreamtime session.
/// </summary>
public static class UnreadCalculator
{
    /// <summary>Dreamtime cron jobs are always named "Dreamtime" (see GatewayService).</summary>
    private const string DreamtimeTaskName = "Dreamtime";

    public static bool Participates(SessionSource? source, string? cronTaskName)
        => source != SessionSource.Chat
           && !string.Equals(cronTaskName, DreamtimeTaskName, StringComparison.Ordinal);

    public static int UnreadFor(MobileSession session, string? cronTaskName, long watermark)
    {
        if (!Participates(session.Source, cronTaskName)) return 0;
        var count = 0;
        foreach (var m in session.Messages)
            if (m is AssistantMessage && m.Timestamp > watermark)
                count++;
        return count;
    }

    public static long CaughtUpTimestamp(MobileSession session)
    {
        long max = 0;
        foreach (var m in session.Messages)
            if (m.Timestamp > max) max = m.Timestamp;
        return max;
    }
}
