namespace Achates.Server.Cron;

/// <summary>
/// Shared format for the hidden first user message that every cron-run session
/// carries. Used by <see cref="CronService"/> to write the marker and by
/// <see cref="CronSessionReaper"/> to recognize cron-origin sessions even when
/// their <c>JobId</c> stamp was lost (e.g. an old chat-resave path).
/// </summary>
public static class CronSessionMarker
{
    private const string Prefix = "[Scheduled task: ";

    public static string FormatHeader(string jobName) => $"{Prefix}{jobName}]";

    /// <summary>
    /// Extracts the job name when <paramref name="text"/> begins with a
    /// <c>[Scheduled task: &lt;name&gt;]</c> marker on its first line; otherwise null.
    /// </summary>
    public static string? TryParseJobName(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var newline = text.IndexOf('\n');
        var firstLine = newline >= 0 ? text[..newline] : text;

        var close = firstLine.IndexOf(']');
        if (close < Prefix.Length) return null;

        var name = firstLine[Prefix.Length..close];
        return name.Length == 0 ? null : name;
    }
}
