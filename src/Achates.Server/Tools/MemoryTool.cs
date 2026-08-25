using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and writes persistent memory files across three scopes plus an archive:
/// <list type="bullet">
/// <item><c>shared</c> — facts every agent should know about the user; fetched on demand.</item>
/// <item><c>agent</c> — the agent's durable core notes, injected into every session by
/// <see cref="MemoryContext"/> (labelled "Core" in everything the model sees).</item>
/// <item><c>working</c> — a short rolling list of live threads, injected on every turn.</item>
/// </list>
/// The archive is a directory of topical files addressed by the separate <c>file</c>
/// parameter, not by a scope value.
///
/// <para>
/// When <c>sharedEnabled</c> is false the shared scope is hidden from the model: the
/// schema keeps <c>scope</c> but narrows its enum to <c>[agent, working]</c>, and
/// <see cref="ExecuteAsync"/> rewrites anything outside that allow-list to <c>agent</c>
/// so a schema-ignoring call can never reach the shared file. This is the
/// roleplay/in-character configuration: it prevents real-world identity facts from
/// polluting in-character context.
/// </para>
/// Memory survives session resets in every mode.
/// </summary>
internal sealed class MemoryTool : AgentTool
{
    private static readonly JsonElement _bothScopesSchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save", "append", "edit", "list", "search"], "Action to perform.", "read"),
            ["scope"] = StringEnum(["shared", "agent", "working"],
                "Which memory to target. " +
                "'shared' = facts about the user that any assistant should know (name, family, preferences, important dates). " +
                "'agent' = your durable core notes; already loaded into every conversation. " +
                "'working' = a short list of live threads to raise next time; also already loaded. Keep it small and prune resolved items.",
                "agent"),
            ["content"] = StringSchema("Text to write. For 'save' it REPLACES the whole memory file for the chosen scope (include everything you want to keep) — slow for large memories. For 'append' it is added to the end. Prefer 'append'/'edit' for routine updates."),
            ["old"] = StringSchema("For 'edit': the exact existing text to replace. Must match a unique substring of the current memory."),
            ["new"] = StringSchema("For 'edit': the replacement text. Use an empty string to delete the matched text."),
            ["file"] = StringSchema("Optional archive file slug (e.g. 'journal', 'marriage-history'). When set, read/save/append/edit target this topical archive file instead of your main memory. Your main memory is small and always loaded; the archive holds detailed or dated notes you retrieve on demand."),
            ["query"] = StringSchema("For 'search': case-insensitive text to find across your archive files."),
        },
        required: ["action"]);

    private static readonly JsonElement _agentOnlySchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save", "append", "edit", "list", "search"], "Action to perform.", "read"),
            ["scope"] = StringEnum(["agent", "working"],
                "Which memory to target. " +
                "'agent' = your durable core notes; already loaded into every conversation. " +
                "'working' = a short list of live threads to raise next time; also already loaded. Keep it small and prune resolved items.",
                "agent"),
            ["content"] = StringSchema("Text to write. For 'save' it REPLACES the whole memory file (include everything you want to keep) — slow for large memories. For 'append' it is added to the end. Prefer 'append'/'edit' for routine updates."),
            ["old"] = StringSchema("For 'edit': the exact existing text to replace. Must match a unique substring of the current memory."),
            ["new"] = StringSchema("For 'edit': the replacement text. Use an empty string to delete the matched text."),
            ["file"] = StringSchema("Optional archive file slug (e.g. 'journal', 'marriage-history'). When set, read/save/append/edit target this topical archive file instead of your main memory. Your main memory is small and always loaded; the archive holds detailed or dated notes you retrieve on demand."),
            ["query"] = StringSchema("For 'search': case-insensitive text to find across your archive files."),
        },
        required: ["action"]);

    private readonly string _sharedPath;
    private readonly string _agentPath;
    private readonly string _workingPath;
    private readonly bool _sharedEnabled;
    private readonly string _archiveDir;
    private readonly int _coreBudgetTokens;
    private readonly int _workingBudgetTokens;

    /// <summary>
    /// Fallback core-memory budget (tokens) when neither the agent nor config sets one.
    /// Sized for a file injected into every session's context, not for one fetched
    /// on demand — agents already over this converge via dreamtime consolidation.
    /// </summary>
    public const int DefaultCoreBudgetTokens = 2000;

    /// <summary>Fallback working-memory budget (tokens) when neither the agent nor config sets one.</summary>
    public const int DefaultWorkingBudgetTokens = 500;

    private const double CharsPerToken = 4.0;

    public MemoryTool(
        string sharedPath,
        string agentPath,
        bool sharedEnabled,
        int coreBudgetTokens = 0,
        string? workingPath = null,
        int workingBudgetTokens = 0)
    {
        _sharedPath = sharedPath;
        _agentPath = agentPath;
        _sharedEnabled = sharedEnabled;
        _coreBudgetTokens = coreBudgetTokens;
        _workingBudgetTokens = workingBudgetTokens;
        var agentDir = Path.GetDirectoryName(Path.GetFullPath(agentPath)) ?? ".";
        _workingPath = workingPath ?? Path.Combine(agentDir, "working.md");
        _archiveDir = Path.Combine(agentDir, "memory");
    }

    /// <summary>Resolution order: per-agent capability → global default → fallback constant.</summary>
    public static int ResolveCoreBudgetTokens(int? perAgent, int? globalDefault) =>
        perAgent ?? globalDefault ?? DefaultCoreBudgetTokens;

    /// <summary>Resolution order: per-agent capability → global default → fallback constant.</summary>
    public static int ResolveWorkingBudgetTokens(int? perAgent, int? globalDefault) =>
        perAgent ?? globalDefault ?? DefaultWorkingBudgetTokens;

    /// <summary>
    /// Non-blocking note appended to a write when estimated tokens exceed the
    /// scope's budget. Empty string when no budget is set or the content fits.
    /// </summary>
    private static string BudgetNote(string content, int budget, string label, string remedy)
    {
        if (budget <= 0) return "";
        var tokens = (int)(content.Length / CharsPerToken);
        if (tokens <= budget) return "";
        return $"\n\n[{label} is now ~{tokens} tokens, over the ~{budget} target. {remedy}]";
    }

    /// <summary>A resolved memory scope: where it lives, what to call it, and its soft budget.</summary>
    private sealed record ScopeTarget(string Path, string Label, int Budget, string Remedy);

    /// <summary>
    /// Maps a scope name to its file. Anything unrecognized resolves to the agent
    /// core file, which is the historical default for a missing scope.
    /// </summary>
    private ScopeTarget Resolve(string scope) => scope switch
    {
        "shared" => new(_sharedPath, "Shared", 0, ""),
        "working" => new(_workingPath, "Working", _workingBudgetTokens,
            "Drop items you have already surfaced or resolved, and move anything durable into core memory."),
        _ => new(_agentPath, "Core", _coreBudgetTokens,
            "Move dated/completed sections into the archive (append/save with a `file`), or summarize verbose event logs there."),
    };

    public override string Name => "memory";
    public override string Description => _sharedEnabled
        ? "Read, save, append to, or edit persistent memory. Use 'shared' scope for universal user facts, 'agent' scope for your durable core notes, 'working' scope for the short list of live threads to raise next time. Prefer append/edit over a full save for small updates."
        : "Read, save, append to, or edit your persistent private notes: 'agent' scope for your durable core notes, 'working' scope for the short list of live threads to raise next time. Survives across sessions. Prefer append/edit over a full save for small updates.";
    public override string Label => "Memory";
    public override JsonElement Parameters => _sharedEnabled ? _bothScopesSchema : _agentOnlySchema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "read";
        // Honor the requested scope, but on a shared-disabled agent allow ONLY the
        // scopes that agent is entitled to. Anything else — "shared", null, a casing
        // variant, an invented value — collapses to "agent". Allow-list, not
        // deny-list: an off-enum string would otherwise miss the scoped-read guard
        // below and fall through to the unscoped branch, which reads the shared file.
        var scope = GetString(arguments, "scope");
        if (!_sharedEnabled && scope is not ("agent" or "working"))
            scope = "agent";

        if (action is "list")
            return ListArchive();

        if (action is "search")
            return SearchArchive(GetString(arguments, "query"));

        var file = GetString(arguments, "file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            return action switch
            {
                "read" => await ReadArchiveAsync(file),
                "save" => await WriteArchiveAsync(file, GetString(arguments, "content"), append: false),
                "append" => await WriteArchiveAsync(file, GetString(arguments, "content"), append: true),
                "edit" => await EditArchiveAsync(file, GetString(arguments, "old"), GetString(arguments, "new")),
                _ => Msg($"Unknown action for an archive file: {action}"),
            };
        }

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
        if (scope is "shared" or "agent" or "working")
        {
            var target = Resolve(scope);

            if (!File.Exists(target.Path))
            {
                return new AgentToolResult
                {
                    Content = [new CompletionTextContent { Text = $"{target.Label} memory is empty. Use save to store information." }],
                };
            }

            var content = await File.ReadAllTextAsync(target.Path);
            var note = BudgetNote(content, target.Budget, $"{target.Label} memory", target.Remedy);
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"## {target.Label} Memory\n\n{content}{note}" }],
            };
        }

        // Unscoped read — every always-loaded tier at once. Only shared-enabled
        // agents reach here: on a shared-disabled agent the caller has already
        // rewritten every scope outside {agent, working} to "agent", so the
        // scoped branch above always claims the call.
        var parts = new List<string>
        {
            await SectionAsync("shared"),
            await SectionAsync("agent"),
            await SectionAsync("working"),
        };

        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = string.Join("\n\n---\n\n", parts) }],
        };

        // One rendered section, budget nudge included — so a budgeted scope
        // signals the same way on an unscoped read as it does on save/append/edit.
        // Shared carries Budget = 0, so it never nudges.
        async Task<string> SectionAsync(string sectionScope)
        {
            var target = Resolve(sectionScope);
            if (!File.Exists(target.Path))
                return $"## {target.Label} Memory\n\n(empty)";

            var content = await File.ReadAllTextAsync(target.Path);
            var note = BudgetNote(content, target.Budget, $"{target.Label} memory", target.Remedy);
            return $"## {target.Label} Memory\n\n{content}{note}";
        }
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

        var target = Resolve(scope);

        var dir = Path.GetDirectoryName(target.Path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(target.Path, content);
        var note = BudgetNote(content, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"{target.Label} memory saved.{note}");
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

        var target = Resolve(scope);
        var dir = Path.GetDirectoryName(target.Path);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var existing = File.Exists(target.Path) ? await File.ReadAllTextAsync(target.Path) : "";
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        var combined = existing + separator + content;
        await File.WriteAllTextAsync(target.Path, combined);

        var note = BudgetNote(combined, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"Appended to {target.Label.ToLowerInvariant()} memory.{note}");
    }

    /// <summary>
    /// Replaces a unique substring of a scope's memory — the cheap path for
    /// correcting or removing a specific fact (an empty replacement deletes it).
    /// Refuses ambiguous or missing matches rather than guess, so the model must
    /// quote enough surrounding text to be unambiguous.
    /// </summary>
    private async Task<AgentToolResult> EditMemoryAsync(string scope, string? oldText, string? newText)
    {
        var target = Resolve(scope);
        if (!File.Exists(target.Path))
            return Msg($"{target.Label} memory is empty — nothing to edit.");

        var existing = await File.ReadAllTextAsync(target.Path);
        var (updated, error) = ReplaceUnique(existing, oldText, newText);
        if (error is not null)
            return Msg(error);

        await File.WriteAllTextAsync(target.Path, updated!);
        var note = BudgetNote(updated!, target.Budget, $"{target.Label} memory", target.Remedy);
        return Msg($"{target.Label} memory updated.{note}");
    }

    private static (string? updated, string? error) ReplaceUnique(string existing, string? oldText, string? newText)
    {
        if (string.IsNullOrEmpty(oldText))
            return (null, "'old' is required when editing (the existing text to replace).");
        var occurrences = CountOccurrences(existing, oldText);
        if (occurrences == 0)
            return (null, "The 'old' text was not found. Read first and copy the exact text to replace.");
        if (occurrences > 1)
            return (null, $"The 'old' text appears {occurrences} times — include more surrounding text so it matches exactly once.");
        return (existing.Replace(oldText, newText ?? ""), null);
    }

    // ---- Archive tier: a directory of topical markdown files retrieved on demand ----

    private async Task<AgentToolResult> ReadArchiveAsync(string file)
    {
        if (!TryResolveArchive(file, out var abs, out var error))
            return Msg(error);
        if (!File.Exists(abs))
            return Msg($"Archive file '{ArchiveSlug(abs)}' not found. Use action:list to see what's archived.");
        var content = await File.ReadAllTextAsync(abs);
        return Msg($"## Archive: {ArchiveSlug(abs)}\n\n{content}");
    }

    private AgentToolResult ListArchive()
    {
        if (!Directory.Exists(_archiveDir))
            return Msg("Your memory archive is empty. Use save/append with a `file` to add a topical note.");

        var files = Directory.EnumerateFiles(_archiveDir, "*.md")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return Msg("Your memory archive is empty. Use save/append with a `file` to add a topical note.");

        var rows = files.Select(f =>
        {
            var slug = Path.GetFileNameWithoutExtension(f);
            var sizeKb = Math.Max(1, (int)(new FileInfo(f).Length / 1024));
            var heading = FirstHeading(f);
            return heading is null ? $"- {slug} (~{sizeKb}KB)" : $"- {slug} — {heading} (~{sizeKb}KB)";
        });

        return Msg($"## Memory Archive\n\n{string.Join("\n", rows)}\n\nUse action:read with a `file` to open one, or action:search to find content.");
    }

    private AgentToolResult SearchArchive(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Msg("A 'query' is required for search.");
        if (!Directory.Exists(_archiveDir))
            return Msg("Your memory archive is empty — nothing to search.");

        const int maxLines = 25;
        var nameHits = new List<string>();
        var bodyHits = new List<string>();

        foreach (var f in Directory.EnumerateFiles(_archiveDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var slug = Path.GetFileNameWithoutExtension(f);
            var heading = FirstHeading(f) ?? "";
            if (slug.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                heading.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                nameHits.Add($"- {slug} — {heading}".TrimEnd(' ', '—'));
            }

            var lineNum = 0;
            foreach (var line in File.ReadLines(f))
            {
                lineNum++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    var snippet = line.Trim();
                    if (snippet.Length > 160) snippet = snippet[..160] + "…";
                    bodyHits.Add($"- {slug}:{lineNum}: {snippet}");
                }
            }
        }

        if (nameHits.Count == 0 && bodyHits.Count == 0)
            return Msg($"No matches for '{query}' in your archive.");

        var sb = new System.Text.StringBuilder();
        sb.Append($"## Archive search: '{query}'\n");
        if (nameHits.Count > 0)
        {
            sb.Append("\n**Matching files:**\n");
            sb.Append(string.Join("\n", nameHits));
            sb.Append('\n');
        }
        if (bodyHits.Count > 0)
        {
            sb.Append("\n**Matching lines:**\n");
            sb.Append(string.Join("\n", bodyHits.Take(maxLines)));
            if (bodyHits.Count > maxLines)
                sb.Append($"\n…and {bodyHits.Count - maxLines} more. Narrow the query or read the file.");
        }
        return Msg(sb.ToString());
    }

    private static string? FirstHeading(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith('#'))
                return t.TrimStart('#').Trim();
            if (t.Length > 0)
                return t.Length > 80 ? t[..80] : t;
        }
        return null;
    }

    private async Task<AgentToolResult> WriteArchiveAsync(string file, string? content, bool append)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Msg("Content is required.");
        if (!TryResolveArchive(file, out var abs, out var error))
            return Msg(error);

        Directory.CreateDirectory(_archiveDir);
        if (append && File.Exists(abs))
        {
            var existing = await File.ReadAllTextAsync(abs);
            var sep = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            await File.WriteAllTextAsync(abs, existing + sep + content);
            return Msg($"Appended to archive file '{ArchiveSlug(abs)}'.");
        }

        await File.WriteAllTextAsync(abs, content);
        return Msg($"{(append ? "Appended to" : "Saved")} archive file '{ArchiveSlug(abs)}'.");
    }

    private async Task<AgentToolResult> EditArchiveAsync(string file, string? oldText, string? newText)
    {
        if (!TryResolveArchive(file, out var abs, out var error))
            return Msg(error);
        if (!File.Exists(abs))
            return Msg($"Archive file '{ArchiveSlug(abs)}' not found.");

        var existing = await File.ReadAllTextAsync(abs);
        var (updated, replaceError) = ReplaceUnique(existing, oldText, newText);
        if (replaceError is not null)
            return Msg(replaceError);

        await File.WriteAllTextAsync(abs, updated!);
        return Msg($"Archive file '{ArchiveSlug(abs)}' updated.");
    }

    private static string ArchiveSlug(string absPath) =>
        Path.GetFileNameWithoutExtension(absPath);

    /// <summary>
    /// Resolves an archive file slug to an absolute path inside <see cref="_archiveDir"/>,
    /// normalizing a missing <c>.md</c> extension and rejecting anything that escapes the
    /// directory (mirrors <c>NotebookTool.TryResolve</c>).
    /// </summary>
    private bool TryResolveArchive(string? file, out string absPath, out string error)
    {
        absPath = "";
        error = "";
        var name = file?.Trim() ?? "";
        if (name.Length == 0)
        {
            error = "A 'file' name is required for archive operations.";
            return false;
        }
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            name += ".md";
        if (Path.IsPathRooted(name))
        {
            error = $"'file' value '{file}' escapes the archive directory. Use a simple name like 'journal'.";
            return false;
        }

        var rootFull = Path.GetFullPath(_archiveDir);
        var combined = Path.GetFullPath(Path.Combine(rootFull, name));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            error = $"'file' value '{file}' escapes the archive directory.";
            return false;
        }

        absPath = combined;
        return true;
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
