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
