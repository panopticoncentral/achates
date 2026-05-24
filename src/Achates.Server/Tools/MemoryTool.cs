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
            ["action"] = StringEnum(["read", "save"], "Action to perform.", "read"),
            ["scope"] = StringEnum(["shared", "agent"],
                "Which memory to target. " +
                "'shared' = facts about the user that any assistant should know (name, family, preferences, important dates). " +
                "'agent' = notes specific to this assistant's role and past conversations.",
                "agent"),
            ["content"] = StringSchema("Content to save. Required when action is 'save'. This replaces the entire memory file for the chosen scope, so include everything you want to keep."),
        },
        required: ["action"]);

    private static readonly JsonElement _agentOnlySchema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save"], "Action to perform.", "read"),
            ["content"] = StringSchema("Content to save. Required when action is 'save'. This replaces the entire memory file, so include everything you want to keep."),
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
        ? "Read or save persistent memory. Use 'shared' scope for universal user facts, 'agent' scope for your own notes."
        : "Read or save your persistent private notes. Survives across sessions.";
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
            _ => new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"Unknown action: {action}" }],
            },
        };
    }

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
        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = $"{label} memory saved." }],
        };
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
