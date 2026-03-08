using Achates.Agent.Tools;

namespace Achates.Server;

/// <summary>
/// Assembles the system prompt from runtime context.
/// </summary>
public static class SystemPrompt
{
    public static string Build(IReadOnlyList<AgentTool>? tools = null)
    {
        var lines = new List<string>
        {
            "You are a helpful personal assistant.",
            "",
        };

        // Inject timezone and date for time-aware reasoning without a tool call.
        // Only timezone is included (not clock time) to keep prompt caching stable.
        var tz = TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        lines.Add("## Current Date & Time");
        lines.Add($"Date: {today:dddd, MMMM d, yyyy}");
        lines.Add($"Timezone: {tz.Id}");
        lines.Add("If you need the exact current time, use the session tool.");
        lines.Add("");

        if (tools is { Count: > 0 })
        {
            lines.Add("## Tools");
            lines.Add("You have the following tools available:");
            foreach (var tool in tools)
            {
                lines.Add($"- {tool.Name}: {tool.Description}");
            }
            lines.Add("");
        }

        lines.Add("## Style");
        lines.Add("Be concise and direct. Lead with the answer, not the reasoning.");
        lines.Add("Use markdown formatting when it improves readability.");

        return string.Join('\n', lines);
    }
}
