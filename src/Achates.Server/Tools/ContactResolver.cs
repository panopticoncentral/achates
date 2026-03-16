using System.Text.Json;
using Achates.Server.Graph;

namespace Achates.Server.Tools;

/// <summary>
/// Resolves phone numbers and email addresses to contact names by fetching contacts
/// from Microsoft Graph API. Results are cached in memory and refreshed periodically.
/// </summary>
internal sealed class ContactResolver(IReadOnlyDictionary<string, GraphClient> graphClients)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private Dictionary<string, string>? _contacts;
    private DateTime _loadedAt;

    /// <summary>
    /// Ensure contacts are loaded from Graph API. Call once before using Resolve().
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_contacts is not null && DateTime.UtcNow - _loadedAt < CacheDuration)
            return;

        _contacts = await LoadContactsAsync(cancellationToken);
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

        if (_contacts is not { Count: > 0 })
            return handleId;

        var normalized = handleId.Contains('@')
            ? handleId.ToLowerInvariant()
            : NormalizePhone(handleId);

        return _contacts.TryGetValue(normalized, out var name) ? name : handleId;
    }

    private async Task<Dictionary<string, string>> LoadContactsAsync(CancellationToken cancellationToken)
    {
        var contacts = new Dictionary<string, string>();

        foreach (var (_, client) in graphClients)
        {
            try
            {
                await LoadFromGraphAsync(client, contacts, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Skip accounts that fail (insufficient permissions, etc.)
            }
        }

        return contacts;
    }

    private static async Task LoadFromGraphAsync(
        GraphClient client, Dictionary<string, string> contacts, CancellationToken cancellationToken)
    {
        var path = "contacts?$select=givenName,surname,emailAddresses,phones&$top=999";

        while (path is not null)
        {
            var result = await client.GetAsync<JsonElement>(path, cancellationToken);

            if (result.TryGetProperty("value", out var items))
            {
                foreach (var contact in items.EnumerateArray())
                {
                    var givenName = contact.TryGetProperty("givenName", out var gn) && gn.ValueKind == JsonValueKind.String
                        ? gn.GetString() : null;
                    var surname = contact.TryGetProperty("surname", out var sn) && sn.ValueKind == JsonValueKind.String
                        ? sn.GetString() : null;

                    var name = BuildName(givenName, surname);
                    if (name is null)
                        continue;

                    // Index email addresses
                    if (contact.TryGetProperty("emailAddresses", out var emails))
                    {
                        foreach (var email in emails.EnumerateArray())
                        {
                            if (email.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String)
                            {
                                var address = addr.GetString();
                                if (address is not null)
                                    contacts.TryAdd(address.ToLowerInvariant(), name);
                            }
                        }
                    }

                    // Index phone numbers
                    if (contact.TryGetProperty("phones", out var phones))
                    {
                        foreach (var phone in phones.EnumerateArray())
                        {
                            if (phone.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.String)
                            {
                                var number = num.GetString();
                                if (number is not null)
                                    contacts.TryAdd(NormalizePhone(number), name);
                            }
                        }
                    }
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

    private static string? BuildName(string? firstName, string? lastName)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length > 0 ? name : null;
    }

    private static string NormalizePhone(string phone) =>
        new(phone.Where(char.IsDigit).ToArray());
}
