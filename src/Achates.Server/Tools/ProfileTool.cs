using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Allows the agent to read and update its own profile: description, prompt, and avatar.
/// </summary>
internal sealed class ProfileTool(string agentDir, Func<CancellationToken, Task> reloadFunc) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["get", "update"], "Action to perform."),
            ["description"] = StringSchema("New agent description. Only used with 'update'."),
            ["prompt"] = StringSchema("New system prompt. Only used with 'update'."),
            ["avatar"] = StringSchema("Avatar image. Provide a file path from the image tool (e.g. '/agents/.../image.jpg') or base64-encoded image data. Only used with 'update'."),
            ["voice"] = StringSchema("Per-agent TTS voice id (e.g. 'af_nicole' or a Kokoro blend). Empty string clears the voice (makes the agent voiceless). Only used with 'update'."),
        },
        required: ["action"]);

    public override string Name => "profile";
    public override string Description =>
        "Read or update your own profile. 'get' returns your current description, prompt, avatar, and voice. " +
        "'update' changes your description, prompt, avatar, and/or voice (only provide fields you want to change).";
    public override string Label => "Profile";
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
            "get" => await GetProfileAsync(cancellationToken),
            "update" => await UpdateProfileAsync(arguments, cancellationToken),
            _ => TextResult("action must be 'get' or 'update'."),
        };
    }

    private async Task<AgentToolResult> GetProfileAsync(CancellationToken ct)
    {
        var agentFile = Path.Combine(agentDir, "AGENT.md");
        if (!File.Exists(agentFile))
            return TextResult("Agent file not found.");

        var content = await File.ReadAllTextAsync(agentFile, ct);
        var config = AgentLoader.Parse(content);
        if (config is null)
            return TextResult("Failed to parse agent file.");

        var parts = new List<CompletionUserContent>();

        var text = $"**Description:** {config.Description ?? "(none)"}\n\n**Prompt:** {config.Prompt ?? "(none)"}\n\n**Voice:** {config.Voice ?? "(none)"}";
        parts.Add(new CompletionTextContent { Text = text });

        // Include avatar if one exists
        var avatarPath = FindAvatarPath();
        if (avatarPath is not null)
        {
            var bytes = await File.ReadAllBytesAsync(avatarPath, ct);
            var mimeType = avatarPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            parts.Add(new CompletionImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType });
        }

        return new AgentToolResult { Content = parts };
    }

    private async Task<AgentToolResult> UpdateProfileAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var newDescription = GetString(arguments, "description");
        var newPrompt = GetString(arguments, "prompt");
        var newAvatar = GetString(arguments, "avatar");
        var newVoice = GetString(arguments, "voice");

        if (newDescription is null && newPrompt is null && newAvatar is null && newVoice is null)
            return TextResult("Provide at least one of: description, prompt, avatar, voice.");

        // Read and parse current config
        var agentFile = Path.Combine(agentDir, "AGENT.md");
        if (!File.Exists(agentFile))
            return TextResult("Agent file not found.");

        var content = await File.ReadAllTextAsync(agentFile, ct);
        var config = AgentLoader.Parse(content);
        if (config is null)
            return TextResult("Failed to parse agent file.");

        // Update only the fields provided
        if (newDescription is not null)
            config.Description = newDescription;
        if (newPrompt is not null)
            config.Prompt = newPrompt;
        if (newVoice is not null)
            config.Voice = string.IsNullOrEmpty(newVoice) ? null : newVoice;

        // Serialize and write back
        var displayName = config.Title ?? Path.GetFileName(agentDir);
        var markdown = AgentLoader.Serialize(displayName, config);

        var tempPath = agentFile + ".tmp";
        await File.WriteAllTextAsync(tempPath, markdown, ct);
        File.Move(tempPath, agentFile, overwrite: true);

        // Handle avatar update
        if (newAvatar is not null)
        {
            byte[] avatarBytes;

            // Accept a file path (from ImageTool) or base64 data
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

            avatarBytes = AvatarImage.Compress(avatarBytes, 512, 80);
            await File.WriteAllBytesAsync(Path.Combine(agentDir, "avatar.jpg"), avatarBytes, ct);

            var pngPath = Path.Combine(agentDir, "avatar.png");
            if (File.Exists(pngPath)) File.Delete(pngPath);
        }

        await reloadFunc(ct);

        var updated = new List<string>();
        if (newDescription is not null) updated.Add("description");
        if (newPrompt is not null) updated.Add("prompt");
        if (newAvatar is not null) updated.Add("avatar");
        if (newVoice is not null) updated.Add("voice");
        return TextResult($"Profile updated: {string.Join(", ", updated)}.");
    }

    private string? FindAvatarPath()
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
}
