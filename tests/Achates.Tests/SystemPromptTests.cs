using Achates.Server;

namespace Achates.Tests;

public sealed class SystemPromptTests
{
    // --- Agent identity ---

    [Fact]
    public void Uses_agent_prompt_when_provided()
    {
        var result = SystemPrompt.Build(agentPrompt: "You are a pirate assistant.");

        Assert.StartsWith("You are a pirate assistant.", result);
    }

    [Fact]
    public void Falls_back_to_description_when_no_prompt()
    {
        var result = SystemPrompt.Build(agentDescription: "a helpful bot");

        Assert.StartsWith("You are a helpful bot.", result);
    }

    [Fact]
    public void Falls_back_to_default_when_nothing_provided()
    {
        var result = SystemPrompt.Build();

        Assert.StartsWith("You are a helpful personal assistant.", result);
    }

    [Fact]
    public void Prompt_takes_precedence_over_description()
    {
        var result = SystemPrompt.Build(
            agentDescription: "ignored",
            agentPrompt: "Custom prompt here.");

        Assert.StartsWith("Custom prompt here.", result);
        Assert.DoesNotContain("ignored", result);
    }

    // --- Date and timezone ---

    [Fact]
    public void Includes_current_date_section()
    {
        var result = SystemPrompt.Build();

        Assert.Contains("## Current Date & Time", result);
        Assert.Contains("Date:", result);
        Assert.Contains("Timezone:", result);
    }

    // --- Memory section (always present) ---

    [Fact]
    public void Always_includes_memory_section()
    {
        var result = SystemPrompt.Build();

        Assert.Contains("## Memory", result);
        Assert.Contains("Shared memory", result);
        Assert.Contains("Agent memory", result);
    }

    // --- Style section (always present) ---

    [Fact]
    public void Always_includes_style_section()
    {
        var result = SystemPrompt.Build();

        Assert.Contains("## Style", result);
        Assert.Contains("Be concise", result);
    }

    // --- Optional sections ---

    [Fact]
    public void Todo_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasTodo: true);

        Assert.Contains("## Todo List", result);
    }

    [Fact]
    public void Todo_section_excluded_when_disabled()
    {
        var result = SystemPrompt.Build(hasTodo: false);

        Assert.DoesNotContain("## Todo List", result);
    }

    [Fact]
    public void Notes_section_uses_custom_folder_name()
    {
        var result = SystemPrompt.Build(hasNotes: true, notesFolderName: "MyNotes");

        Assert.Contains("## Notes", result);
        Assert.Contains("'MyNotes'", result);
    }

    [Fact]
    public void Notes_section_defaults_to_achates_folder()
    {
        var result = SystemPrompt.Build(hasNotes: true);

        Assert.Contains("'Achates'", result);
    }

    [Fact]
    public void Mail_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasMail: true);

        Assert.Contains("## Mail", result);
    }

    [Fact]
    public void Mail_section_shows_accounts_when_multiple()
    {
        var result = SystemPrompt.Build(hasMail: true, graphAccountNames: ["personal", "work"]);

        Assert.Contains("Available accounts: personal, work", result);
    }

    [Fact]
    public void Mail_section_omits_accounts_when_single()
    {
        var result = SystemPrompt.Build(hasMail: true, graphAccountNames: ["personal"]);

        Assert.DoesNotContain("Available accounts", result);
    }

    [Fact]
    public void Calendar_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasCalendar: true);

        Assert.Contains("## Calendar", result);
    }

    [Fact]
    public void Calendar_section_shows_accounts_when_multiple()
    {
        var result = SystemPrompt.Build(hasCalendar: true, graphAccountNames: ["a", "b"]);

        Assert.Contains("Available accounts: a, b", result);
    }

    [Fact]
    public void Web_search_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasWebSearch: true);

        Assert.Contains("## Web Search", result);
    }

    [Fact]
    public void Web_fetch_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasWebFetch: true);

        Assert.Contains("## Web Fetch", result);
    }

    [Fact]
    public void Cost_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasCost: true);

        Assert.Contains("## Cost Tracking", result);
    }

    [Fact]
    public void IMessage_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasIMessage: true);

        Assert.Contains("## iMessage", result);
    }

    [Fact]
    public void Health_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasHealth: true);

        Assert.Contains("## Health", result);
    }

    [Fact]
    public void Cron_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasCron: true);

        Assert.Contains("## Scheduled Tasks", result);
    }

    [Fact]
    public void Disabled_sections_are_excluded()
    {
        var result = SystemPrompt.Build();

        Assert.DoesNotContain("## Todo List", result);
        Assert.DoesNotContain("## Notes", result);
        Assert.DoesNotContain("## Mail", result);
        Assert.DoesNotContain("## Calendar", result);
        Assert.DoesNotContain("## Web Search", result);
        Assert.DoesNotContain("## Web Fetch", result);
        Assert.DoesNotContain("## Cost Tracking", result);
        Assert.DoesNotContain("## iMessage", result);
        Assert.DoesNotContain("## Health", result);
        Assert.DoesNotContain("## Scheduled Tasks", result);
    }

    // --- Tools section ---

    [Fact]
    public void Tools_section_excluded_when_no_tools()
    {
        var result = SystemPrompt.Build();

        Assert.DoesNotContain("## Tools", result);
    }
}
