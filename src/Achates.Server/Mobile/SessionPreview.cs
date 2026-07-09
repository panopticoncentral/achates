using System.Text.RegularExpressions;
using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Mobile;

/// <summary>
/// Computes the one-line preview shown under a session title in the list.
/// Walks messages newest-first for the last visible message of either role, so
/// the hidden <c>[Scheduled task: ...]</c> marker on cron/dreamtime sessions
/// never leaks into the UI as the row subtitle.
/// </summary>
public static partial class SessionPreview
{
    public static string? Compute(MobileSession session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var text = session.Messages[i] switch
            {
                UserMessage { Hidden: false } user => user.Text,
                AssistantMessage assistant => string.Join(" ", assistant.Content
                    .OfType<CompletionTextContent>()
                    .Select(c => c.Text)),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
                return Whitespace().Replace(text, " ").Trim();
        }

        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
