using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Tools;

internal sealed class TimeTool : AgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "timezone": {
                    "type": "string",
                    "description": "IANA timezone (e.g., 'America/New_York', 'Asia/Tokyo'). Defaults to local time."
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    public override string Name => "get_current_time";
    public override string Description => "Get the current date and time, optionally in a specific timezone.";
    public override string Label => "Current Time";
    public override JsonElement Parameters => Schema;

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

        return Task.FromResult(new AgentToolResult
        {
            Content = [new CompletionTextContent { Text = now.ToString("yyyy-MM-dd HH:mm:ss zzz") }],
        });
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
