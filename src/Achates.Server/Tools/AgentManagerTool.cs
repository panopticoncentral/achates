using System.Globalization;
using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Lets an agent manage agents at runtime: list every agent, read or modify any
/// agent's definition (including avatar and ID rename), and create new agents.
/// </summary>
internal sealed class AgentManagerTool(
    string agentsDir,
    Func<string, CancellationToken, Task> loadFunc,
    Func<string, string, string, CancellationToken, Task> renameFunc) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read", "modify", "create"],
                "list: enumerate all agents. read: get one agent's full definition. " +
                "modify: change an existing agent's definition. create: make a new agent."),
            ["agent"] = StringSchema("Agent id (directory name). Required for 'read' and 'modify'."),
            ["name"] = StringSchema("Display name. Required for 'create'. For 'modify', changing it renames the agent (its id is re-derived from the name)."),
            ["description"] = StringSchema("Agent description. Required for 'create'; optional for 'modify'."),
            ["prompt"] = StringSchema("System prompt. Required for 'create'; optional for 'modify'."),
            ["tools"] = ArraySchema(StringSchema("Tool name."), "Tool names. Optional. For 'modify' this replaces the whole list."),
            ["model"] = StringSchema("Base model id. Optional ('modify' only)."),
            ["thinking_model"] = StringSchema("Thinking model id. Optional ('modify' only)."),
            ["provider"] = StringSchema("Provider name. Optional ('modify' only)."),
            ["reasoning_effort"] = StringSchema("Reasoning effort, e.g. 'low'/'medium'/'high'. Optional ('modify' only)."),
            ["temperature"] = NumberSchema("Sampling temperature. Optional ('modify' only)."),
            ["max_tokens"] = NumberSchema("Max output tokens. Optional ('modify' only)."),
            ["allowed_chats"] = ArraySchema(StringSchema("Agent id."), "Agents this agent may chat with. Optional ('modify' only); replaces the whole list."),
            ["dreamtime"] = StringSchema("Dreamtime, e.g. '3:00 AM', or 'off' to disable. Optional ('modify' only)."),
            ["shared_memory"] = BooleanSchema("When false, the agent's memory tool only sees its own private notes (the universal user memory at ~/.achates/memory.md is hidden). Useful for roleplay or in-character agents. Optional ('modify' only)."),
            ["voice"] = StringSchema("Per-agent TTS voice id (e.g. 'af_nicole' or a Kokoro blend). Empty string clears the voice. Only used with 'modify' or 'create'."),
            ["speech_rate"] = NumberSchema("Per-agent TTS rate. 1.0 is normal speed; Kokoro accepts [0.25, 4.0]. Practical range: 0.85–1.25. Pass 0 to revert to default (1.0). Only used with 'modify' or 'create'."),
            ["avatar"] = StringSchema("Avatar image: a file path from the image tool or base64-encoded data. Optional ('modify' only)."),
        },
        required: ["action"]);

    public override string Name => "agent_manager";
    public override string Description =>
        "Manage agents at runtime. Actions: 'list' (all agents), 'read' (one agent's full definition), " +
        "'modify' (change any agent's description, prompt, tools, model, avatar, etc., or rename it), " +
        "'create' (make a new agent).";
    public override string Label => "Agent Manager";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action");

        return action switch
        {
            "list" => ListAgents(),
            "read" => await ReadAgentAsync(arguments, cancellationToken),
            "modify" => await ModifyAgentAsync(arguments, cancellationToken),
            "create" => await CreateAgentAsync(arguments, cancellationToken),
            _ => TextResult("action must be 'list', 'read', 'modify', or 'create'."),
        };
    }

    private AgentToolResult ListAgents()
    {
        if (!Directory.Exists(agentsDir))
            return TextResult("No agents found.");

        var sb = new StringBuilder();
        foreach (var dir in Directory.GetDirectories(agentsDir).OrderBy(d => d))
        {
            var id = Path.GetFileName(dir);
            var agentFile = Path.Combine(dir, "AGENT.md");
            if (!File.Exists(agentFile)) continue;

            var config = AgentLoader.Parse(File.ReadAllText(agentFile));
            if (config is null)
            {
                sb.AppendLine($"- {id} — (failed to parse AGENT.md)");
                continue;
            }

            var display = config.Title ?? id;
            var desc = config.Description ?? "(no description)";
            var tools = config.Tools is { Count: > 0 } ? string.Join(", ", config.Tools) : "none";
            var model = config.Model ?? "(default)";
            var voiceSuffix = config.Voice is not null ? $"; voice: {config.Voice}" : "";
            sb.AppendLine($"- {id} — {display}: {desc} (tools: {tools}; model: {model}{voiceSuffix})");
        }

        var text = sb.Length == 0 ? "No agents found." : sb.ToString().TrimEnd();
        return TextResult(text);
    }

    private async Task<AgentToolResult> ReadAgentAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var id = GetString(arguments, "agent");
        if (string.IsNullOrWhiteSpace(id))
            return TextResult("'agent' is required for 'read'.");

        var agentDir = Path.Combine(agentsDir, id);
        var agentFile = Path.Combine(agentDir, "AGENT.md");
        if (!File.Exists(agentFile))
            return TextResult($"Agent '{id}' not found.");

        var config = AgentLoader.Parse(await File.ReadAllTextAsync(agentFile, ct));
        if (config is null)
            return TextResult($"Failed to parse AGENT.md for agent '{id}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"**Id:** {id}");
        sb.AppendLine($"**Display name:** {config.Title ?? id}");
        sb.AppendLine($"**Description:** {config.Description ?? "(none)"}");
        sb.AppendLine($"**Provider:** {config.Provider ?? "(default)"}");
        sb.AppendLine($"**Model:** {config.Model ?? "(default)"}");
        sb.AppendLine($"**Thinking model:** {config.ThinkingModel ?? "(default)"}");
        sb.AppendLine($"**Tools:** {(config.Tools is { Count: > 0 } ? string.Join(", ", config.Tools) : "(none)")}");
        sb.AppendLine($"**Allowed chats:** {(config.AllowChat is { Count: > 0 } ? string.Join(", ", config.AllowChat) : "(all)")}");
        sb.AppendLine($"**Reasoning effort:** {config.Completion?.ReasoningEffort ?? "(default)"}");
        sb.AppendLine($"**Temperature:** {config.Completion?.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "(default)"}");
        sb.AppendLine($"**Max tokens:** {config.Completion?.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "(default)"}");
        sb.AppendLine($"**Dreamtime:** {config.Dreamtime?.ToString("h:mm tt", CultureInfo.InvariantCulture) ?? "(off)"}");
        sb.AppendLine($"**Shared memory:** {(config.SharedMemory == false ? "disabled" : "enabled")}");
        sb.AppendLine($"**Voice:** {config.Voice ?? "(none)"}");
        sb.AppendLine($"**Speech rate:** {config.SpeechRate?.ToString(CultureInfo.InvariantCulture) ?? "(default)"}");
        sb.AppendLine();
        sb.AppendLine($"**Prompt:**\n{config.Prompt ?? "(none)"}");

        var parts = new List<CompletionUserContent>
        {
            new CompletionTextContent { Text = sb.ToString().TrimEnd() },
        };

        var avatarPath = FindAvatarPath(agentDir);
        if (avatarPath is not null)
        {
            var bytes = await File.ReadAllBytesAsync(avatarPath, ct);
            var mimeType = avatarPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            parts.Add(new CompletionImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType });
        }

        return new AgentToolResult { Content = parts };
    }

    private async Task<AgentToolResult> ModifyAgentAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var id = GetString(arguments, "agent");
        if (string.IsNullOrWhiteSpace(id))
            return TextResult("'agent' is required for 'modify'.");

        var agentDir = Path.Combine(agentsDir, id);
        var agentFile = Path.Combine(agentDir, "AGENT.md");
        if (!File.Exists(agentFile))
            return TextResult($"Agent '{id}' not found.");

        var config = AgentLoader.Parse(await File.ReadAllTextAsync(agentFile, ct));
        if (config is null)
            return TextResult($"Failed to parse AGENT.md for agent '{id}'.");

        var newName = GetString(arguments, "name");
        var newDescription = GetString(arguments, "description");
        var newPrompt = GetString(arguments, "prompt");
        var newTools = GetStringList(arguments, "tools");
        var newModel = GetString(arguments, "model");
        var newThinkingModel = GetString(arguments, "thinking_model");
        var newProvider = GetString(arguments, "provider");
        var newReasoning = GetString(arguments, "reasoning_effort");
        var newTemperature = GetDouble(arguments, "temperature");
        var newMaxTokens = GetInt(arguments, "max_tokens");
        var newAllowedChats = GetStringList(arguments, "allowed_chats");
        var hasDreamtime = arguments.ContainsKey("dreamtime");
        var newDreamtime = GetString(arguments, "dreamtime");
        var hasSharedMemory = arguments.ContainsKey("shared_memory");
        var newSharedMemory = GetBool(arguments, "shared_memory");
        var hasVoice = arguments.ContainsKey("voice");
        var newVoice = GetString(arguments, "voice");
        var hasSpeechRate = arguments.ContainsKey("speech_rate");
        var newSpeechRate = GetDouble(arguments, "speech_rate");
        var newAvatar = GetString(arguments, "avatar");

        var anyField = newName is not null || newDescription is not null || newPrompt is not null
            || newTools is not null || newModel is not null || newThinkingModel is not null
            || newProvider is not null || newReasoning is not null || newTemperature is not null
            || newMaxTokens is not null || newAllowedChats is not null || hasDreamtime
            || hasSharedMemory || hasVoice || hasSpeechRate
            || newAvatar is not null;
        if (!anyField)
            return TextResult("Provide at least one field to modify.");

        var changed = new List<string>();

        if (newDescription is not null) { config.Description = newDescription; changed.Add("description"); }
        if (newPrompt is not null) { config.Prompt = newPrompt; changed.Add("prompt"); }
        if (newTools is not null) { config.Tools = newTools; changed.Add("tools"); }
        if (newModel is not null) { config.Model = newModel; changed.Add("model"); }
        if (newThinkingModel is not null) { config.ThinkingModel = newThinkingModel; changed.Add("thinking_model"); }
        if (newProvider is not null) { config.Provider = newProvider; changed.Add("provider"); }
        if (newAllowedChats is not null) { config.AllowChat = newAllowedChats; changed.Add("allowed_chats"); }

        if (newReasoning is not null || newTemperature is not null || newMaxTokens is not null)
            config.Completion ??= new CompletionConfig();
        if (newReasoning is not null) { config.Completion!.ReasoningEffort = newReasoning; changed.Add("reasoning_effort"); }
        if (newTemperature is not null) { config.Completion!.Temperature = newTemperature; changed.Add("temperature"); }
        if (newMaxTokens is not null) { config.Completion!.MaxTokens = newMaxTokens; changed.Add("max_tokens"); }

        if (hasDreamtime)
        {
            if (string.IsNullOrWhiteSpace(newDreamtime)
                || newDreamtime.Equals("off", StringComparison.OrdinalIgnoreCase)
                || newDreamtime.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                config.Dreamtime = null;
            }
            else if (TimeOnly.TryParse(newDreamtime, CultureInfo.InvariantCulture, out var t))
            {
                config.Dreamtime = t;
            }
            else
            {
                return TextResult($"Could not parse dreamtime '{newDreamtime}'. Use a time like '3:00 AM' or 'off'.");
            }
            changed.Add("dreamtime");
        }

        if (hasSharedMemory)
        {
            // GetBool returns null when the value wasn't a JSON bool, which clears the
            // override (Serialize emits no line for null or true, only for false).
            config.SharedMemory = newSharedMemory;
            changed.Add("shared_memory");
        }

        if (hasVoice)
        {
            config.Voice = string.IsNullOrEmpty(newVoice) ? null : newVoice;
            changed.Add("voice");
        }

        if (hasSpeechRate)
        {
            // 0 / negative / non-numeric clears back to default (null). Anything positive is clamped.
            config.SpeechRate = newSpeechRate is > 0 ? Speech.SpeechRate.Clamp(newSpeechRate.Value) : null;
            changed.Add("speech_rate");
        }

        // Determine rename before writing so we serialize with the final display name.
        var newId = id;
        if (newName is not null)
        {
            var normalized = AgentLoader.NormalizeId(newName);
            if (normalized is null)
                return TextResult("Invalid name — must contain at least one alphanumeric character.");
            newId = normalized;
            if (newId != id && Directory.Exists(Path.Combine(agentsDir, newId)))
                return TextResult($"An agent '{newId}' already exists.");
            config.Title = newName;
            changed.Add("name");
        }

        var displayName = newName ?? config.Title ?? id;
        var markdown = AgentLoader.Serialize(displayName, config);
        var tempPath = agentFile + ".tmp";
        await File.WriteAllTextAsync(tempPath, markdown, ct);
        File.Move(tempPath, agentFile, overwrite: true);

        if (newAvatar is not null)
        {
            byte[] avatarBytes;
            var resolvedPath = AvatarImage.TryResolveImagePath(newAvatar, agentDir);
            if (resolvedPath is not null)
            {
                if (!File.Exists(resolvedPath))
                    return TextResult($"Image file not found: {newAvatar}");
                avatarBytes = await File.ReadAllBytesAsync(resolvedPath, ct);
            }
            else
            {
                try
                {
                    avatarBytes = Convert.FromBase64String(newAvatar);
                }
                catch (FormatException)
                {
                    return TextResult("Invalid avatar. Provide a file path from the image tool or base64-encoded image data.");
                }
            }

            avatarBytes = AvatarImage.Compress(avatarBytes);
            await File.WriteAllBytesAsync(Path.Combine(agentDir, "avatar.jpg"), avatarBytes, ct);
            var pngPath = Path.Combine(agentDir, "avatar.png");
            if (File.Exists(pngPath)) File.Delete(pngPath);
            changed.Add("avatar");
        }

        if (newId != id)
            await renameFunc(id, newId, displayName, ct);
        else
            await loadFunc(id, ct);

        var renameNote = newId != id ? $" Renamed '{id}' → '{newId}'." : "";
        return TextResult($"Agent '{newId}' updated: {string.Join(", ", changed)}.{renameNote}");
    }

    private async Task<AgentToolResult> CreateAgentAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var name = GetString(arguments, "name");
        var description = GetString(arguments, "description");
        var prompt = GetString(arguments, "prompt");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(prompt))
            return TextResult("name, description, and prompt are all required for 'create'.");

        var agentId = AgentLoader.NormalizeId(name);
        if (agentId is null)
            return TextResult("Invalid agent name — must contain at least one alphanumeric character.");

        var agentDir = Path.Combine(agentsDir, agentId);
        if (Directory.Exists(agentDir))
            return TextResult($"An agent '{agentId}' already exists.");

        var tools = GetStringList(arguments, "tools");
        var voice = GetString(arguments, "voice");
        var speechRate = GetDouble(arguments, "speech_rate");

        var config = new AgentConfig
        {
            Description = description,
            Prompt = prompt,
            Tools = tools is { Count: > 0 } ? tools : null,
            Voice = string.IsNullOrEmpty(voice) ? null : voice,
            SpeechRate = speechRate is > 0 ? Speech.SpeechRate.Clamp(speechRate.Value) : null,
        };

        var markdown = AgentLoader.Serialize(name, config);

        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "AGENT.md"), markdown, ct);

        await loadFunc(agentId, ct);

        return TextResult($"Agent '{name}' (id: {agentId}) created successfully.");
    }

    private static string? FindAvatarPath(string agentDir)
    {
        var jpgPath = Path.Combine(agentDir, "avatar.jpg");
        if (File.Exists(jpgPath)) return jpgPath;
        var pngPath = Path.Combine(agentDir, "avatar.png");
        if (File.Exists(pngPath)) return pngPath;
        return null;
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private static double? GetDouble(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number ? je.GetDouble() : null;
        return val is double d ? d : null;
    }

    private static int? GetInt(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Number ? je.GetInt32() : null;
        return val is int i ? i : null;
    }

    private static bool? GetBool(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        return val is bool b ? b : null;
    }

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
        return list;
    }
}
