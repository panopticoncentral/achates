using Achates.Agent.Tools;

namespace Achates.Server;

/// <summary>
/// Assembles the system prompt from agent config and runtime context.
/// </summary>
public static class SystemPrompt
{
    public static string Build(
        string? agentDescription = null,
        string? agentPrompt = null,
        IReadOnlyList<AgentTool>? tools = null)
    {
        var lines = new List<string>();

        // Agent identity
        if (agentPrompt is not null)
        {
            lines.Add(agentPrompt);
        }
        else if (agentDescription is not null)
        {
            lines.Add($"You are {agentDescription}.");
        }
        else
        {
            lines.Add("You are a helpful personal assistant.");
        }

        lines.Add("");

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

        // Memory section — always included since memory tool is added per-session
        lines.Add("## Memory");
        lines.Add("You have a persistent memory file that survives session resets.");
        lines.Add("Read it at the start of new conversations to recall prior context.");
        lines.Add("Save important facts, preferences, and decisions the user would expect you to remember.");
        lines.Add("When saving, include everything you want to keep — the file is replaced, not appended.");
        lines.Add("");

        lines.Add("## Style");
        lines.Add("Be concise and direct. Lead with the answer, not the reasoning.");
        lines.Add("Use markdown formatting when it improves readability.");

        return string.Join('\n', lines);
    }
}
