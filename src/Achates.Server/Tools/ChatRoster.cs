namespace Achates.Server.Tools;

/// <summary>
/// Groups <c>chat</c> rows that represent the same conversation.
///
/// Messages shows one conversation per participant set, but the database keeps a
/// separate row per service — a thread that moved between RCS, SMS and iMessage is
/// several rows with the same people in each. Reading one row in isolation returns a
/// slice of the conversation with nothing to indicate the rest exists.
///
/// Rows group only when both their participants and their conversation name match.
/// Handles are compared as raw addresses and names as trimmed text, both
/// case-insensitively. No phone-number normalization is attempted, and a renamed
/// thread whose sibling rows kept the old name stays split. Both choices under-merge
/// rather than wrongly folding two conversations together — the costlier mistake,
/// since a spliced transcript reads as one real conversation.
/// </summary>
internal static class ChatRoster
{
    /// <summary>Separates handles within a key; no handle contains it.</summary>
    private const char HandleSeparator = '\u001F';

    /// <summary>Separates the conversation name from the roster in a key.</summary>
    private const char NameSeparator = '\u001E';

    /// <summary>
    /// Maps every chat id in <paramref name="rows"/> to the ascending list of chat
    /// ids sharing its exact participant set and name, always including itself. A
    /// chat with no roster rows is absent; callers fall back to it alone.
    /// </summary>
    public static Dictionary<long, List<long>> GroupByRoster(
        IEnumerable<(long ChatId, string? DisplayName, string Handle)> rows)
    {
        var rosters = new Dictionary<long, SortedSet<string>>();
        var names = new Dictionary<long, string>();

        foreach (var (chatId, displayName, handle) in rows)
        {
            if (string.IsNullOrWhiteSpace(handle))
                continue;

            if (!rosters.TryGetValue(chatId, out var roster))
                rosters[chatId] = roster = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            roster.Add(handle.Trim());
            names[chatId] = displayName?.Trim() ?? "";
        }

        // Name plus roster is the grouping key: the same conversation produces the
        // same key, while a differing name or a subset of the participants produces
        // a different one.
        var byKey = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chatId, roster) in rosters)
        {
            var key = string.Concat(
                names[chatId],
                NameSeparator,
                string.Join(HandleSeparator, roster));

            if (!byKey.TryGetValue(key, out var group))
                byKey[key] = group = [];

            group.Add(chatId);
        }

        var siblings = new Dictionary<long, List<long>>();
        foreach (var group in byKey.Values)
        {
            group.Sort();
            foreach (var chatId in group)
                siblings[chatId] = group;
        }

        return siblings;
    }
}
