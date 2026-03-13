using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and writes persistent memory files. Supports a shared memory (facts all agents
/// should know about the user) and a per-agent memory (agent-specific notes).
/// Memory survives session resets.
/// </summary>
internal sealed class MemoryTool(string sharedPath, string agentPath) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
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

    public override string Name => "memory";
    public override string Description => "Read or save persistent memory. Use 'shared' scope for universal user facts, 'agent' scope for your own notes.";
    public override string Label => "Memory";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "read";
        var scope = GetString(arguments, "scope") ?? "agent";

        return action switch
        {
            "read" => await ReadMemoryAsync(scope),
            "save" => await SaveMemoryAsync(scope, GetString(arguments, "content")),
            _ => new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"Unknown action: {action}" }],
            },
        };
    }

    private async Task<AgentToolResult> ReadMemoryAsync(string scope)
    {
        if (scope is "shared" or "agent")
        {
            var path = scope == "shared" ? sharedPath : agentPath;
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

        // Default: read both
        var parts = new List<string>();

        if (File.Exists(sharedPath))
        {
            var shared = await File.ReadAllTextAsync(sharedPath);
            parts.Add($"## Shared Memory\n\n{shared}");
        }
        else
        {
            parts.Add("## Shared Memory\n\n(empty)");
        }

        if (File.Exists(agentPath))
        {
            var agent = await File.ReadAllTextAsync(agentPath);
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

        var path = scope == "shared" ? sharedPath : agentPath;

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
