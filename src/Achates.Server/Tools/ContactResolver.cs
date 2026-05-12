using System.Text.Json;
using Achates.Server.Graph;

namespace Achates.Server.Tools;

/// <summary>
/// Loads the user's Outlook contacts from Microsoft Graph and caches them in memory.
/// Exposes both a handle-to-name lookup (for IMessageTool) and the full contact list
/// (for ContactsTool).
/// </summary>
internal sealed class ContactResolver(IReadOnlyDictionary<string, GraphClient> graphClients)
{
    public sealed record Contact(
        string Account,
        string Id,
        string DisplayName,
        IReadOnlyList<string> Emails,
        IReadOnlyList<string> Phones);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private Dictionary<string, string>? _handleIndex;
    private List<Contact>? _all;
    private DateTime _loadedAt;

    /// <summary>
    /// Ensure contacts are loaded from Graph API. Call once before using Resolve(), All, or Search().
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_handleIndex is not null && DateTime.UtcNow - _loadedAt < CacheDuration)
            return;

        var (index, all) = await LoadContactsAsync(cancellationToken);
        _handleIndex = index;
        _all = all;
        _loadedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Resolve a handle ID (phone number or email) to a contact name.
    /// Returns the original handle ID if no match is found.
    /// Call EnsureLoadedAsync() before first use.
    /// </summary>
    public string Resolve(string? handleId)
    {
        if (string.IsNullOrWhiteSpace(handleId))
            return "Unknown";

        if (_handleIndex is not { Count: > 0 })
            return handleId;

        var normalized = handleId.Contains('@')
            ? handleId.ToLowerInvariant()
            : NormalizePhone(handleId);

        return _handleIndex.TryGetValue(normalized, out var name) ? name : handleId;
    }

    /// <summary>
    /// Full list of cached contacts. Call EnsureLoadedAsync() before access.
    /// </summary>
    public IReadOnlyList<Contact> All => _all ?? (IReadOnlyList<Contact>)[];

    /// <summary>
    /// Case-insensitive substring search across display name, emails, and (digit-normalized) phones.
    /// Returns up to <paramref name="limit"/> matches in display-name order.
    /// </summary>
    public IEnumerable<Contact> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || _all is null)
            return [];

        var q = query.Trim().ToLowerInvariant();
        var qDigits = NormalizePhone(query);
        var matchPhone = qDigits.Length >= 3;

        return _all
            .Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Emails.Any(e => e.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (matchPhone && c.Phones.Any(p => NormalizePhone(p).Contains(qDigits))))
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit);
    }

    private async Task<(Dictionary<string, string> Index, List<Contact> All)> LoadContactsAsync(
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, string>();
        var all = new List<Contact>();

        foreach (var (account, client) in graphClients)
        {
            try
            {
                await LoadFromGraphAsync(account, client, index, all, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Skip accounts that fail (insufficient permissions, etc.)
            }
        }

        return (index, all);
    }

    private static async Task LoadFromGraphAsync(
        string account, GraphClient client, Dictionary<string, string> index,
        List<Contact> all, CancellationToken cancellationToken)
    {
        var path = "contacts?$select=id,givenName,surname,displayName,emailAddresses,phones&$top=999";

        while (path is not null)
        {
            var result = await client.GetAsync<JsonElement>(path, cancellationToken);

            if (result.TryGetProperty("value", out var items))
            {
                foreach (var contact in items.EnumerateArray())
                {
                    var givenName = GetStringProp(contact, "givenName");
                    var surname = GetStringProp(contact, "surname");
                    var displayName = GetStringProp(contact, "displayName")
                        ?? BuildName(givenName, surname);
                    if (displayName is null)
                        continue;

                    var id = GetStringProp(contact, "id");
                    if (id is null)
                        continue;

                    var emails = new List<string>();
                    if (contact.TryGetProperty("emailAddresses", out var emailEls))
                    {
                        foreach (var email in emailEls.EnumerateArray())
                        {
                            var address = GetStringProp(email, "address");
                            if (address is null) continue;
                            emails.Add(address);
                            index.TryAdd(address.ToLowerInvariant(), displayName);
                        }
                    }

                    var phones = new List<string>();
                    if (contact.TryGetProperty("phones", out var phoneEls))
                    {
                        foreach (var phone in phoneEls.EnumerateArray())
                        {
                            var number = GetStringProp(phone, "number");
                            if (number is null) continue;
                            phones.Add(number);
                            index.TryAdd(NormalizePhone(number), displayName);
                        }
                    }

                    all.Add(new Contact(account, id, displayName, emails, phones));
                }
            }

            // Follow pagination
            path = result.TryGetProperty("@odata.nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? nextLink.GetString() : null;

            // nextLink is a full URL — strip the base path prefix to get the relative path
            if (path is not null && path.StartsWith("https://graph.microsoft.com/v1.0/", StringComparison.OrdinalIgnoreCase))
            {
                // Extract everything after /me/ or /users/{email}/
                var meIndex = path.IndexOf("/me/", StringComparison.OrdinalIgnoreCase);
                if (meIndex >= 0)
                    path = path[(meIndex + 4)..];
                else
                {
                    var usersIndex = path.IndexOf("/users/", StringComparison.OrdinalIgnoreCase);
                    if (usersIndex >= 0)
                    {
                        // Skip /users/{email}/
                        var afterUsers = path[(usersIndex + 7)..];
                        var slashIndex = afterUsers.IndexOf('/');
                        path = slashIndex >= 0 ? afterUsers[(slashIndex + 1)..] : afterUsers;
                    }
                }
            }
        }
    }

    private static string? GetStringProp(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? BuildName(string? firstName, string? lastName)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length > 0 ? name : null;
    }

    private static string NormalizePhone(string phone) =>
        new(phone.Where(char.IsDigit).ToArray());
}
