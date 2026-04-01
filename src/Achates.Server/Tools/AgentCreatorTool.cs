using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Allows an agent to create new agents at runtime.
/// </summary>
internal sealed class AgentCreatorTool(string agentsDir, Func<string, CancellationToken, Task> loadFunc) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["name"] = StringSchema("Display name for the new agent."),
            ["description"] = StringSchema("Description of the new agent."),
            ["prompt"] = StringSchema("System prompt for the new agent."),
            ["model"] = StringSchema("Model ID (e.g. 'anthropic/claude-sonnet-4'). Optional."),
            ["tools"] = ArraySchema(StringSchema("Tool name."), "List of tool names. Optional."),
        },
        required: ["name", "description", "prompt"]);

    public override string Name => "agent_creator";
    public override string Description =>
        "Create a new agent. Provide a name, description, and prompt. Optionally specify a model and tools.";
    public override string Label => "Create Agent";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var name = GetString(arguments, "name");
        var description = GetString(arguments, "description");
        var prompt = GetString(arguments, "prompt");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(prompt))
            return TextResult("name, description, and prompt are all required.");

        var agentId = AgentLoader.NormalizeId(name);
        if (agentId is null)
            return TextResult("Invalid agent name — must contain at least one alphanumeric character.");

        var agentDir = Path.Combine(agentsDir, agentId);
        if (Directory.Exists(agentDir))
            return TextResult($"An agent '{agentId}' already exists.");

        var model = GetString(arguments, "model");
        var tools = GetStringList(arguments, "tools");

        var config = new AgentConfig
        {
            Description = description,
            Prompt = prompt,
            Model = model,
            Tools = tools,
        };

        var markdown = AgentLoader.Serialize(name, config);

        Directory.CreateDirectory(agentDir);
        var agentFile = Path.Combine(agentDir, "AGENT.md");
        await File.WriteAllTextAsync(agentFile, markdown, cancellationToken);

        await loadFunc(agentId, cancellationToken);

        return TextResult($"Agent '{name}' (id: {agentId}) created successfully.");
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static List<string>? GetStringList(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is not JsonElement je || je.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in je.EnumerateArray())
        {
            var s = item.GetString();
            if (s is not null) list.Add(s);
        }
        return list.Count > 0 ? list : null;
    }
}
