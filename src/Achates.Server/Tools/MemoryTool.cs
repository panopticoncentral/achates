using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Reads and writes a per-peer persistent memory file. Memory survives session resets.
/// </summary>
internal sealed class MemoryTool(string filePath) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["read", "save"], "Action to perform.", "read"),
            ["content"] = StringSchema("Content to save. Required when action is 'save'. This replaces the entire memory file, so include everything you want to keep."),
        },
        required: ["action"]);

    public override string Name => "memory";
    public override string Description => "Read or save your persistent memory file. Memory survives session resets (/new).";
    public override string Label => "Memory";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "read";

        return action switch
        {
            "read" => await ReadMemoryAsync(),
            "save" => await SaveMemoryAsync(GetString(arguments, "content")),
            _ => new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = $"Unknown action: {action}" }],
            },
        };
    }

    private async Task<AgentToolResult> ReadMemoryAsync()
    {
        if (!File.Exists(filePath))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Memory is empty. Use save to store information." }],
            };
        }

        var content = await File.ReadAllTextAsync(filePath);
        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = content }],
        };
    }

    private async Task<AgentToolResult> SaveMemoryAsync(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AgentToolResult
            {
                Content = [new CompletionTextContent { Text = "Content is required when saving." }],
            };
        }

        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, content);
        return new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = "Memory saved." }],
        };
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
