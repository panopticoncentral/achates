using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and writes persistent memory files. When <c>sharedEnabled</c>
/// is true, exposes both a shared memory (facts every agent should know about
/// the user) and a per-agent memory (agent-specific notes). When false, the
/// shared scope is hidden from the model entirely — the schema lists no
/// <c>scope</c> parameter and reads/saves only the agent-local file. This is
/// the roleplay/in-character configuration: it prevents real-world identity
/// facts from polluting in-character context.
/// Memory survives session resets in both modes.
/// </summary>
internal sealed class MemoryTool : AgentTool
{
    private static readonly JsonElement _bothScopesSchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save", "append", "edit"], "Action to perform.", "read"),
            ["scope"] = StringEnum(["shared", "agent"],
                "Which memory to target. " +
                "'shared' = facts about the user that any assistant should know (name, family, preferences, important dates). " +
                "'agent' = notes specific to this assistant's role and past conversations.",
                "agent"),
            ["content"] = StringSchema("Text to write. For 'save' it REPLACES the whole memory file for the chosen scope (include everything you want to keep) — slow for large memories. For 'append' it is added to the end. Prefer 'append'/'edit' for routine updates."),
            ["old"] = StringSchema("For 'edit': the exact existing text to replace. Must match a unique substring of the current memory."),
            ["new"] = StringSchema("For 'edit': the replacement text. Use an empty string to delete the matched text."),
        },
        required: ["action"]);

    private static readonly JsonElement _agentOnlySchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save", "append", "edit"], "Action to perform.", "read"),
            ["content"] = StringSchema("Text to write. For 'save' it REPLACES the whole memory file (include everything you want to keep) — slow for large memories. For 'append' it is added to the end. Prefer 'append'/'edit' for routine updates."),
            ["old"] = StringSchema("For 'edit': the exact existing text to replace. Must match a unique substring of the current memory."),
            ["new"] = StringSchema("For 'edit': the replacement text. Use an empty string to delete the matched text."),
        },
        required: ["action"]);

    private readonly string _sharedPath;
    private readonly string _agentPath;
    private readonly bool _sharedEnabled;

    public MemoryTool(string sharedPath, string agentPath, bool sharedEnabled)
    {
        _sharedPath = sharedPath;
        _agentPath = agentPath;
        _sharedEnabled = sharedEnabled;
    }

    public override string Name => "memory";
    public override string Description => _sharedEnabled
        ? "Read, save, append to, or edit persistent memory. Use 'shared' scope for universal user facts, 'agent' scope for your own notes. Prefer append/edit over a full save for small updates."
        : "Read, save, append to, or edit your persistent private notes. Survives across sessions. Prefer append/edit over a full save for small updates.";
    public override string Label => "Memory";
    public override JsonElement Parameters => _sharedEnabled ? _bothScopesSchema : _agentOnlySchema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "read";
        // When shared is disabled, force every request to agent scope — defensive
        // against hand-crafted calls or schema-disrespecting models. The shared
        // file is never touched in that mode.
        // When shared is enabled, a missing scope means "read both" (null stays null).
        var scope = _sharedEnabled ? GetString(arguments, "scope") : "agent";

        return action switch
        {
            "read" => await ReadMemoryAsync(scope),
            "save" => await SaveMemoryAsync(scope ?? "agent", GetString(arguments, "content")),
            "append" => await AppendMemoryAsync(scope ?? "agent", GetString(arguments, "content")),
            "edit" => await EditMemoryAsync(scope ?? "agent", GetString(arguments, "old"), GetString(arguments, "new")),
            _ => Msg($"Unknown action: {action}"),
        };
    }

    private static AgentToolResult Msg(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private async Task<AgentToolResult> ReadMemoryAsync(string? scope)
    {
        // Scoped reads (one file).
        if (scope is "shared" or "agent")
        {
            var path = scope == "shared" ? _sharedPath : _agentPath;
            var label = scope == "shared" ? "Shared" : "Agent";

            if (!File.Exists(path))
            {
                return new AgentToolResult
                {
                    Content = [new CompletionTextContent { Text = $"{label} memory is empty. Use save to store information." }],
                };
            }

            var content = await File.ReadAllTextAsync(path);
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"## {label} Memory\n\n{content}" }],
            };
        }

        // Unscoped read — only reachable in shared-enabled mode (in disabled
        // mode `scope` is forced to "agent" above).
        var parts = new List<string>();

        if (File.Exists(_sharedPath))
        {
            var shared = await File.ReadAllTextAsync(_sharedPath);
            parts.Add($"## Shared Memory\n\n{shared}");
        }
        else
        {
            parts.Add("## Shared Memory\n\n(empty)");
        }

        if (File.Exists(_agentPath))
        {
            var agent = await File.ReadAllTextAsync(_agentPath);
            parts.Add($"## Agent Memory\n\n{agent}");
        }
        else
        {
            parts.Add("## Agent Memory\n\n(empty)");
        }

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = string.Join("\n\n---\n\n", parts) }],
        };
    }

    private async Task<AgentToolResult> SaveMemoryAsync(string scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Content is required when saving." }],
            };
        }

        var path = scope == "shared" ? _sharedPath : _agentPath;

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content);
        var label = scope == "shared" ? "Shared" : "Agent";
        return Msg($"{label} memory saved.");
    }

    /// <summary>
    /// Appends text to the end of a scope's memory without rewriting the rest —
    /// the cheap path for adding a new learning. Keeps payloads small so a routine
    /// nightly update never has to regenerate the whole file as a single tool call.
    /// </summary>
    private async Task<AgentToolResult> AppendMemoryAsync(string scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Msg("Content is required when appending.");
        }

        var path = scope == "shared" ? _sharedPath : _agentPath;
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        await File.WriteAllTextAsync(path, existing + separator + content);

        var label = scope == "shared" ? "Shared" : "Agent";
        return Msg($"Appended to {label.ToLowerInvariant()} memory.");
    }

    /// <summary>
    /// Replaces a unique substring of a scope's memory — the cheap path for
    /// correcting or removing a specific fact (an empty replacement deletes it).
    /// Refuses ambiguous or missing matches rather than guess, so the model must
    /// quote enough surrounding text to be unambiguous.
    /// </summary>
    private async Task<AgentToolResult> EditMemoryAsync(string scope, string? oldText, string? newText)
    {
        var label = scope == "shared" ? "Shared" : "Agent";

        if (string.IsNullOrEmpty(oldText))
        {
            return Msg("'old' is required when editing (the existing text to replace).");
        }

        var path = scope == "shared" ? _sharedPath : _agentPath;
        if (!File.Exists(path))
        {
            return Msg($"{label} memory is empty — nothing to edit.");
        }

        var existing = await File.ReadAllTextAsync(path);
        var occurrences = CountOccurrences(existing, oldText);
        if (occurrences == 0)
        {
            return Msg("The 'old' text was not found. Read the memory first and copy the exact text to replace.");
        }
        if (occurrences > 1)
        {
            return Msg($"The 'old' text appears {occurrences} times — include more surrounding text so it matches exactly once.");
        }

        var updated = existing.Replace(oldText, newText ?? "");
        await File.WriteAllTextAsync(path, updated);
        return Msg($"{label} memory updated.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
