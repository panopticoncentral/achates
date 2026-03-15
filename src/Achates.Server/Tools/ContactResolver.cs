using Microsoft.Data.Sqlite;

namespace Achates.Server.Tools;

/// <summary>
/// Resolves phone numbers and email addresses to contact names by reading the macOS AddressBook databases.
/// Results are cached in memory and refreshed periodically.
/// </summary>
internal sealed class ContactResolver
{
    private static readonly string AddressBookPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "AddressBook");

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private Dictionary<string, string>? _contacts;
    private DateTime _loadedAt;

    /// <summary>
    /// Resolve a handle ID (phone number or email) to a contact name.
    /// Returns the original handle ID if no match is found.
    /// </summary>
    public string Resolve(string? handleId)
    {
        if (string.IsNullOrWhiteSpace(handleId))
            return "Unknown";

        EnsureLoaded();

        if (_contacts is not { Count: > 0 })
            return handleId;

        var normalized = handleId.Contains('@')
            ? handleId.ToLowerInvariant()
            : NormalizePhone(handleId);

        return _contacts.TryGetValue(normalized, out var name) ? name : handleId;
    }

    private void EnsureLoaded()
    {
        if (_contacts is not null && DateTime.UtcNow - _loadedAt < CacheDuration)
            return;

        _contacts = LoadContacts();
        _loadedAt = DateTime.UtcNow;
    }

    private static Dictionary<string, string> LoadContacts()
    {
        var contacts = new Dictionary<string, string>();

        if (!Directory.Exists(AddressBookPath))
            return contacts;

        string[] dbFiles;
        try
        {
            dbFiles = Directory.GetFiles(AddressBookPath, "AddressBook-v22.abcddb", SearchOption.AllDirectories);
        }
        catch
        {
            return contacts;
        }

        foreach (var dbFile in dbFiles)
        {
            try
            {
                LoadFromDatabase(dbFile, contacts);
            }
            catch (SqliteException)
            {
                // Skip inaccessible databases (FDA not granted, corrupt, etc.)
            }
        }

        return contacts;
    }

    private static void LoadFromDatabase(string dbPath, Dictionary<string, string> contacts)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();

        // Load phone number mappings
        using var phoneCmd = conn.CreateCommand();
        phoneCmd.CommandText = """
            SELECT r.ZFIRSTNAME, r.ZLASTNAME, p.ZFULLNUMBER
            FROM ZABCDPHONENUMBER p
            INNER JOIN ZABCDRECORD r ON p.ZOWNER = r.Z_PK
            WHERE (r.ZFIRSTNAME IS NOT NULL OR r.ZLASTNAME IS NOT NULL)
              AND p.ZFULLNUMBER IS NOT NULL
            """;

        using var phoneReader = phoneCmd.ExecuteReader();
        while (phoneReader.Read())
        {
            var name = BuildName(
                phoneReader.IsDBNull(0) ? null : phoneReader.GetString(0),
                phoneReader.IsDBNull(1) ? null : phoneReader.GetString(1));
            var phone = phoneReader.GetString(2);

            if (name is not null)
            {
                var normalized = NormalizePhone(phone);
                contacts.TryAdd(normalized, name);
            }
        }

        // Load email mappings
        using var emailCmd = conn.CreateCommand();
        emailCmd.CommandText = """
            SELECT r.ZFIRSTNAME, r.ZLASTNAME, e.ZADDRESS
            FROM ZABCDEMAILADDRESS e
            INNER JOIN ZABCDRECORD r ON e.ZOWNER = r.Z_PK
            WHERE (r.ZFIRSTNAME IS NOT NULL OR r.ZLASTNAME IS NOT NULL)
              AND e.ZADDRESS IS NOT NULL
            """;

        using var emailReader = emailCmd.ExecuteReader();
        while (emailReader.Read())
        {
            var name = BuildName(
                emailReader.IsDBNull(0) ? null : emailReader.GetString(0),
                emailReader.IsDBNull(1) ? null : emailReader.GetString(1));
            var email = emailReader.GetString(2);

            if (name is not null)
                contacts.TryAdd(email.ToLowerInvariant(), name);
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
