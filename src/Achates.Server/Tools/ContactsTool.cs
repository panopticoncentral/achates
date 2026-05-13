using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Graph;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Searches and reads the user's Outlook contacts via Microsoft Graph.
/// </summary>
internal sealed class ContactsTool(
    IReadOnlyDictionary<string, GraphClient> graphClients,
    ContactResolver contacts) : AgentTool
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly JsonElement _schema = ObjectSchema(
        BuildSchemaProperties(graphClients),
        required: ["action"]);

    public override string Name => "contacts";
    public override string Description =>
        "Look up the user's Outlook contacts: search by name/email/phone, read one contact's full details, or list contacts.";
    public override string Label => "Contacts";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "search";

        return action switch
        {
            "search" => await SearchAsync(arguments, cancellationToken),
            "list" => await ListAsync(arguments, cancellationToken),
            "read" => await ReadAsync(arguments, cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> SearchAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return TextResult("query is required for 'search'.");

        var limit = Math.Clamp(GetInt(arguments, "limit", DefaultLimit), 1, MaxLimit);

        await contacts.EnsureLoadedAsync(cancellationToken);
        var hits = contacts.Search(query, limit).ToList();
        return FormatList(hits, $"Search results for \"{query}\"",
            emptyHint: $"No contacts matched \"{query}\".");
    }

    private async Task<AgentToolResult> ListAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(GetInt(arguments, "limit", DefaultLimit), 1, MaxLimit);

        await contacts.EnsureLoadedAsync(cancellationToken);
        var slice = contacts.All
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return FormatList(slice, $"Contacts (showing {slice.Count} of {contacts.All.Count})",
            emptyHint: "Your Outlook personal Contacts folder appears to be empty.");
    }

    private async Task<AgentToolResult> ReadAsync(
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var id = GetString(arguments, "id");
        if (string.IsNullOrWhiteSpace(id))
            return TextResult("id is required for 'read'.");

        var graph = ResolveClient(graphClients, arguments);

        // Graph v1.0 Contact has no `phones` collection — only the legacy
        // homePhones/businessPhones/mobilePhone fields. Requesting `phones`
        // returns HTTP 400 from /v1.0/me/contacts.
        var path = $"contacts/{Uri.EscapeDataString(id)}" +
            "?$select=id,displayName,givenName,surname,emailAddresses," +
            "businessPhones,homePhones,mobilePhone,companyName,jobTitle," +
            "personalNotes,birthday,homeAddress,businessAddress";

        JsonElement contact;
        try
        {
            contact = await graph.GetAsync<JsonElement>(path, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return TextResult($"Failed to read contact: {ex.Message}");
        }

        return FormatFull(contact);
    }

    private AgentToolResult FormatList(IReadOnlyList<ContactResolver.Contact> hits, string header, string emptyHint)
    {
        if (hits.Count == 0)
        {
            var errors = contacts.LoadErrors;
            if (errors.Count > 0)
            {
                var sb2 = new StringBuilder();
                sb2.AppendLine("No contacts available. Errors loading from Graph:");
                foreach (var (account, message) in errors)
                    sb2.AppendLine($"- {account}: {message}");
                return TextResult(sb2.ToString().TrimEnd());
            }
            return TextResult(emptyHint);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**{header}** ({hits.Count}):");
        sb.AppendLine();

        var i = 1;
        foreach (var c in hits)
        {
            sb.AppendLine($"{i++}. **{c.DisplayName}**");
            if (c.Emails.Count > 0)
                sb.AppendLine($"   email: {string.Join(", ", c.Emails)}");
            if (c.Phones.Count > 0)
                sb.AppendLine($"   phone: {string.Join(", ", c.Phones)}");
            sb.AppendLine($"   id: `{c.Id}` (account: {c.Account})");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static AgentToolResult FormatFull(JsonElement contact)
    {
        var sb = new StringBuilder();

        var displayName = GetStringProp(contact, "displayName")
            ?? BuildName(GetStringProp(contact, "givenName"), GetStringProp(contact, "surname"))
            ?? "(no name)";
        sb.AppendLine($"**{displayName}**");

        var company = GetStringProp(contact, "companyName");
        var title = GetStringProp(contact, "jobTitle");
        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(company))
        {
            var line = string.Join(" — ",
                new[] { title, company }.Where(s => !string.IsNullOrWhiteSpace(s))!);
            sb.AppendLine(line);
        }
        sb.AppendLine();

        if (contact.TryGetProperty("emailAddresses", out var emails) && emails.ValueKind == JsonValueKind.Array)
        {
            foreach (var email in emails.EnumerateArray())
            {
                var address = GetStringProp(email, "address");
                if (address is null) continue;
                var name = GetStringProp(email, "name");
                sb.AppendLine(name is not null && name != address
                    ? $"Email: {address} ({name})"
                    : $"Email: {address}");
            }
        }

        if (contact.TryGetProperty("phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
        {
            foreach (var phone in phones.EnumerateArray())
            {
                var number = GetStringProp(phone, "number");
                if (number is null) continue;
                var type = GetStringProp(phone, "type");
                sb.AppendLine(type is not null
                    ? $"Phone ({type}): {number}"
                    : $"Phone: {number}");
            }
        }

        AppendStringArray(sb, contact, "businessPhones", "Business phone");
        AppendStringArray(sb, contact, "homePhones", "Home phone");

        var mobile = GetStringProp(contact, "mobilePhone");
        if (!string.IsNullOrWhiteSpace(mobile))
            sb.AppendLine($"Mobile: {mobile}");

        AppendAddress(sb, contact, "homeAddress", "Home address");
        AppendAddress(sb, contact, "businessAddress", "Business address");

        var birthday = GetStringProp(contact, "birthday");
        if (!string.IsNullOrWhiteSpace(birthday))
            sb.AppendLine($"Birthday: {birthday}");

        var notes = GetStringProp(contact, "personalNotes");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine();
            sb.AppendLine("**Notes:**");
            sb.AppendLine(notes);
        }

        var id = GetStringProp(contact, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            sb.AppendLine();
            sb.AppendLine($"id: `{id}`");
        }

        return TextResult(sb.ToString().TrimEnd());
    }

    private static void AppendStringArray(StringBuilder sb, JsonElement el, string prop, string label)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    sb.AppendLine($"{label}: {s}");
            }
        }
    }

    private static void AppendAddress(StringBuilder sb, JsonElement el, string prop, string label)
    {
        if (!el.TryGetProperty(prop, out var addr) || addr.ValueKind != JsonValueKind.Object)
            return;

        var parts = new[] { "street", "city", "state", "postalCode", "countryOrRegion" }
            .Select(p => GetStringProp(addr, p))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (parts.Count > 0)
            sb.AppendLine($"{label}: {string.Join(", ", parts)}");
    }

    private static string? GetStringProp(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? BuildName(string? firstName, string? lastName)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length > 0 ? name : null;
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static int GetInt(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return defaultValue;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : defaultValue;
        return val is int i ? i : defaultValue;
    }

    private static Dictionary<string, JsonElement> BuildSchemaProperties(
        IReadOnlyDictionary<string, GraphClient> clients)
    {
        var props = new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["search", "read", "list"], "Action to perform.", "search"),
            ["query"] = StringSchema("Substring to match against name, email, or phone. Case-insensitive. Required for 'search'."),
            ["id"] = StringSchema("Contact ID returned from a prior 'search' or 'list'. Required for 'read'."),
            ["limit"] = NumberSchema($"Max contacts to return. Default {DefaultLimit}, max {MaxLimit}. Used by 'search' and 'list'."),
        };

        if (clients.Count > 1)
            props["account"] = StringEnum([.. clients.Keys], "Account to use for 'read'. Defaults to the contact's source account when omitted.");

        return props;
    }

    private static GraphClient ResolveClient(
        IReadOnlyDictionary<string, GraphClient> clients,
        Dictionary<string, object?> arguments)
    {
        var account = GetString(arguments, "account");
        if (account is not null && clients.TryGetValue(account, out var named))
            return named;
        return clients.Values.First();
    }
}
