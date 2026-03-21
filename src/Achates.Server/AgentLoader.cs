namespace Achates.Server;

/// <summary>
/// Discovers and loads agents from AGENT.md files in ~/.achates/agents/{name}/.
///
/// Format is pure markdown:
///   # Agent Name
///   Description paragraph(s).
///   ## Capabilities
///   - **Model:** anthropic/claude-sonnet-4
///   - **Tools:** session, memory, todo
///   - **Allow chat:** val, claire
///   - **Reasoning effort:** medium
///   ## Prompt
///   System prompt content...
/// </summary>
public static class AgentLoader
{
    private const string AgentFileName = "AGENT.md";

    public static Dictionary<string, AgentConfig> LoadAgents(string achatesHome)
    {
        var agentsDir = Path.Combine(achatesHome, "agents");
        if (!Directory.Exists(agentsDir))
            return new();

        var agents = new Dictionary<string, AgentConfig>();

        foreach (var dir in Directory.GetDirectories(agentsDir))
        {
            var agentFile = Path.Combine(dir, AgentFileName);
            if (!File.Exists(agentFile))
                continue;

            var content = File.ReadAllText(agentFile);
            var config = Parse(content);
            if (config is null)
                continue;

            var name = Path.GetFileName(dir);
            agents[name] = config;
        }

        return agents;
    }

    public static string Serialize(string name, AgentConfig config)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {name}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(config.Description))
        {
            sb.AppendLine(config.Description);
            sb.AppendLine();
        }

        sb.AppendLine("## Capabilities");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(config.Model))
            sb.AppendLine($"**Model:** {config.Model}");

        if (!string.IsNullOrWhiteSpace(config.Provider))
            sb.AppendLine($"**Provider:** {config.Provider}");

        if (config.Tools is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("**Tools:**");
            foreach (var tool in config.Tools)
                sb.AppendLine($"  - {tool}");
        }

        if (config.AllowChat is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("**Allowed Chats:**");
            foreach (var chat in config.AllowChat)
                sb.AppendLine($"  - {chat}");
        }

        if (config.Completion?.ReasoningEffort is { } re)
        {
            sb.AppendLine();
            sb.AppendLine($"**Reasoning Effort:** {re}");
        }

        if (config.Completion?.Temperature is { } temp)
        {
            sb.AppendLine();
            sb.AppendLine($"**Temperature:** {temp}");
        }

        if (config.Completion?.MaxTokens is { } mt)
        {
            sb.AppendLine();
            sb.AppendLine($"**Max Tokens:** {mt}");
        }

        if (!string.IsNullOrWhiteSpace(config.Prompt))
        {
            sb.AppendLine();
            sb.AppendLine("## Prompt");
            sb.AppendLine();
            sb.AppendLine(config.Prompt);
        }

        return sb.ToString();
    }

    public static void CreateDefault(string achatesHome)
    {
        var agentDir = Path.Combine(achatesHome, "agents", "default");
        Directory.CreateDirectory(agentDir);

        var agentFile = Path.Combine(agentDir, AgentFileName);
        if (File.Exists(agentFile))
            return;

        File.WriteAllText(agentFile, """
            # Default

            A helpful assistant.

            ## Capabilities

            **Model:** anthropic/claude-sonnet-4

            **Tools:**
              - session
              - memory

            **Reasoning Effort:** medium

            ## Prompt

            You are a helpful assistant.
            """.Replace("            ", ""));
    }

    internal static AgentConfig? Parse(string content)
    {
        var sections = ParseSections(content);
        if (sections.Count == 0)
            return null;

        var config = new AgentConfig();

        // Description is the body text under the H1 (before any H2)
        if (sections.TryGetValue("", out var description))
            config.Description = description.Trim();

        // Capabilities section — bullet list with **Key:** value
        if (sections.TryGetValue("capabilities", out var capabilities))
            ParseCapabilities(config, capabilities);

        // Prompt section — everything is the system prompt
        if (sections.TryGetValue("prompt", out var prompt))
            config.Prompt = prompt.Trim();

        return config;
    }

    /// <summary>
    /// Splits markdown into sections keyed by lowercase H2 heading.
    /// The "" key holds content between the H1 and first H2 (the description).
    /// </summary>
    private static Dictionary<string, string> ParseSections(string content)
    {
        var sections = new Dictionary<string, string>();
        var lines = content.Split('\n');
        string? currentKey = null;
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("# ") && currentKey is null)
            {
                // H1 — start collecting description
                currentKey = "";
                continue;
            }

            if (line.StartsWith("## "))
            {
                // Save previous section
                if (currentKey is not null)
                    sections[currentKey] = string.Join('\n', currentLines);

                currentKey = line[3..].Trim().ToLowerInvariant();
                currentLines = [];
                continue;
            }

            if (currentKey is not null)
                currentLines.Add(line);
        }

        // Save last section
        if (currentKey is not null)
            sections[currentKey] = string.Join('\n', currentLines);

        return sections;
    }

    /// <summary>
    /// Parses capabilities from a format like:
    ///   **Model:** anthropic/claude-sonnet-4
    ///   **Tools:**
    ///     - session
    ///     - memory
    ///   **Reasoning Effort:** medium
    ///
    /// A **Key:** line can have an inline value or be followed by sub-bullet items.
    /// </summary>
    private static void ParseCapabilities(AgentConfig config, string section)
    {
        string? currentKey = null;
        string? inlineValue = null;
        List<string>? listValues = null;

        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();

            // Check for a **Key:** line
            if (trimmed.StartsWith("**") && trimmed.Contains(":**"))
            {
                // Flush previous key
                if (currentKey is not null)
                    ApplyCapability(config, currentKey, inlineValue, listValues);

                var boldEnd = trimmed.IndexOf(":**", 2, StringComparison.Ordinal);
                currentKey = trimmed[2..boldEnd].Trim().ToLowerInvariant();
                inlineValue = trimmed[(boldEnd + 3)..].Trim();
                listValues = null;
                continue;
            }

            // Check for a sub-bullet under the current key
            if (currentKey is not null && trimmed.StartsWith("- "))
            {
                listValues ??= [];
                listValues.Add(trimmed[2..].Trim());
            }
        }

        // Flush last key
        if (currentKey is not null)
            ApplyCapability(config, currentKey, inlineValue, listValues);
    }

    private static void ApplyCapability(AgentConfig config, string key, string? inlineValue,
        List<string>? listValues)
    {
        // List values take precedence; fall back to comma-splitting the inline value
        List<string>? ResolveList() =>
            listValues is { Count: > 0 }
                ? listValues
                : !string.IsNullOrEmpty(inlineValue)
                    ? inlineValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList()
                    : null;

        var value = inlineValue;

        switch (key)
        {
            case "model":
                config.Model = value;
                break;
            case "provider":
                config.Provider = value;
                break;
            case "tools":
                config.Tools = ResolveList();
                break;
            case "allowed chats" or "allow chat":
                config.AllowChat = ResolveList();
                break;
            case "reasoning effort":
                config.Completion ??= new CompletionConfig();
                config.Completion.ReasoningEffort = value;
                break;
            case "temperature":
                if (double.TryParse(value, out var temp))
                {
                    config.Completion ??= new CompletionConfig();
                    config.Completion.Temperature = temp;
                }
                break;
            case "max tokens":
                if (int.TryParse(value, out var maxTokens))
                {
                    config.Completion ??= new CompletionConfig();
                    config.Completion.MaxTokens = maxTokens;
                }
                break;
        }
    }
}
