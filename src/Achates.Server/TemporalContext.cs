using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;

namespace Achates.Server;

/// <summary>
/// Injects an ephemeral temporal-context note onto the latest user message in the
/// outgoing completion payload — never persisted to <c>AgentRuntime.Messages</c>.
///
/// <para>
/// This replaces the older pattern of baking a fresh date block into the cached
/// system prompt at runtime construction. That approach froze the model's view of
/// "now" at the moment the runtime was built — fine for a single request, but wrong
/// for sessions a user revisits across hours (e.g. replying to a cron-spawned
/// session in the evening still saw the morning's date).
/// </para>
///
/// <para>
/// Putting the note at the tail of the conversation instead keeps the system prompt
/// and tool schemas byte-stable across all turns of all sessions of the same agent —
/// the largest prefix is fully cacheable on both Anthropic (explicit cache_control)
/// and OpenAI (automatic prefix caching) via OpenRouter. The transform recomputes
/// the note only when a new user turn begins (detected by latest user message
/// timestamp), so within-turn tool-call iterations keep the same prefix.
/// </para>
/// </summary>
public static class TemporalContext
{
    /// <summary>Default elapsed-time threshold below which the gap line is omitted.</summary>
    public static readonly TimeSpan DefaultGapThreshold = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds a transform delegate suitable for <see cref="Agent.AgentOptions.TransformContext"/>.
    /// The returned delegate is stateful (caches the most recent computed note) and
    /// expected to be bound to a single <see cref="Agent.AgentRuntime"/>.
    /// </summary>
    /// <param name="clock">Source of "now". Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="gapThreshold">Minimum elapsed time before the gap line appears. Defaults to 30 minutes.</param>
    public static Func<CompletionContext, CompletionContext> CreateTransform(
        Func<DateTimeOffset>? clock = null,
        TimeSpan? gapThreshold = null)
    {
        var nowFn = clock ?? (() => DateTimeOffset.UtcNow);
        var threshold = gapThreshold ?? DefaultGapThreshold;

        long lastSeenUserTimestamp = -1;
        string? cachedNote = null;

        return context =>
        {
            // Find the latest user message in the outgoing payload. If the tail is
            // a tool-result (a within-turn iteration), the cached note still
            // applies to that same user message and we re-inject it there.
            int latestUserIdx = -1;
            CompletionUserMessage? latestUser = null;
            for (var i = context.Messages.Count - 1; i >= 0; i--)
            {
                if (context.Messages[i] is CompletionUserMessage cum)
                {
                    latestUser = cum;
                    latestUserIdx = i;
                    break;
                }
            }
            if (latestUser is null) return context;

            // Recompute only when a new user turn has been added.
            if (cachedNote is null || latestUser.Timestamp != lastSeenUserTimestamp)
            {
                long? previousActivityMs = null;
                if (latestUserIdx > 0)
                {
                    var prev = context.Messages[latestUserIdx - 1];
                    if (prev.Timestamp > 0) previousActivityMs = prev.Timestamp;
                }

                var previousActivityUtc = previousActivityMs is long ms
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : (DateTime?)null;

                cachedNote = FormatNote(nowFn(), previousActivityUtc, threshold);
                lastSeenUserTimestamp = latestUser.Timestamp;
            }

            if (string.IsNullOrEmpty(cachedNote)) return context;

            return InjectIntoUserMessage(context, latestUserIdx, cachedNote);
        };
    }

    /// <summary>
    /// Builds the note text. Always includes the current local time; includes a
    /// "Previous message was Δ ago" line only when the gap meets <paramref name="gapThreshold"/>.
    /// </summary>
    public static string FormatNote(
        DateTimeOffset now,
        DateTime? previousActivityUtc,
        TimeSpan gapThreshold)
    {
        var tz = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var head = $"{local:dddd, MMMM d, yyyy, h:mm tt} ({tz.Id})";

        string? gap = null;
        if (previousActivityUtc is DateTime prevUtc)
        {
            var elapsed = now.UtcDateTime - prevUtc;
            if (elapsed >= gapThreshold)
            {
                var prevLocal = TimeZoneInfo.ConvertTime(
                    new DateTimeOffset(prevUtc, TimeSpan.Zero), tz);
                var sameDay = prevLocal.Date == local.Date;
                var prevAt = sameDay
                    ? $"{prevLocal:h:mm tt}"
                    : $"{prevLocal:ddd, MMM d, h:mm tt}";
                gap = $"Previous message was {FormatElapsed(elapsed)} ago at {prevAt}.";
            }
        }

        return gap is null
            ? $"[Current time: {head}]"
            : $"[Current time: {head}. {gap}]";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1)
        {
            var days = (int)elapsed.TotalDays;
            var hours = elapsed.Hours;
            return $"{days}d {hours}h";
        }
        if (elapsed.TotalHours >= 1)
        {
            var hours = (int)elapsed.TotalHours;
            var minutes = elapsed.Minutes;
            return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
        }
        var mins = Math.Max(1, (int)elapsed.TotalMinutes);
        return $"{mins}m";
    }

    private static CompletionContext InjectIntoUserMessage(
        CompletionContext context, int targetIndex, string note)
    {
        var messages = context.Messages.ToList();
        var target = messages[targetIndex];

        messages[targetIndex] = target switch
        {
            CompletionUserTextMessage text => text with { Text = $"{note}\n\n{text.Text}" },
            CompletionUserContentMessage content => content with
            {
                Content =
                [
                    new CompletionTextContent { Text = note },
                    .. content.Content,
                ],
            },
            _ => target, // Unknown user-message subtype; leave alone.
        };

        return context with { Messages = messages };
    }
}
