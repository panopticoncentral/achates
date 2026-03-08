using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Providers.Models;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Returns a status card with session information: current time, model, provider, context window.
/// </summary>
internal sealed class SessionTool(Model model) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(new Dictionary<string, JsonElement>
    {
        ["timezone"] = StringSchema("IANA timezone (e.g., 'America/New_York'). Defaults to local time."),
    });

    public override string Name => "session";
    public override string Description => "Get current session status: time, model, and configuration.";
    public override string Label => "Session Information";
    public override JsonElement Parameters => _schema;

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var tzName = GetString(arguments, "timezone");
        var tz = tzName is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(tzName)
            : TimeZoneInfo.Local;

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);

        var sb = new StringBuilder();
        sb.AppendLine($"Time: {now:dddd, MMMM d, yyyy — HH:mm:ss zzz}");
        sb.AppendLine($"Timezone: {tz.Id}");
        sb.AppendLine($"Model: {model.Name} ({model.Id})");
        sb.AppendLine($"Provider: {model.Provider.Id}");
        sb.AppendLine($"Context window: {model.ContextWindow:N0} tokens");

        return Task.FromResult(new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = sb.ToString().TrimEnd() }],
        });
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
