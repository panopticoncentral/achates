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
    public void Build_does_not_include_date_section()
    {
        // The system prompt is fully date-free. Temporal context is injected
        // per-turn at the tail of the outgoing payload by TemporalContext,
        // keeping this prompt byte-stable across sessions (cache-friendly).
        var result = SystemPrompt.Build();

        Assert.DoesNotContain("## Current Date & Time", result);
        Assert.DoesNotContain("Timezone:", result);
    }

    // --- Memory section (always present) ---

    [Fact]
    public void Includes_both_memory_scopes_when_shared_enabled()
    {
        var result = SystemPrompt.Build(sharedMemoryEnabled: true);

        Assert.Contains("## Memory", result);
        Assert.Contains("Shared memory", result);
        Assert.Contains("Agent memory", result);
    }

    [Fact]
    public void Default_is_shared_enabled()
    {
        // The parameter defaults to true, so the default-arg call must still
        // produce the dual-scope block. Existing callers don't break.
        var result = SystemPrompt.Build();

        Assert.Contains("## Memory", result);
        Assert.Contains("Shared memory", result);
        Assert.Contains("Agent memory", result);
    }

    [Fact]
    public void Omits_shared_memory_when_shared_disabled()
    {
        var result = SystemPrompt.Build(sharedMemoryEnabled: false);

        Assert.Contains("## Memory", result);
        // The shared scope must not be named, described, or hinted at.
        Assert.DoesNotContain("Shared memory", result);
        Assert.DoesNotContain("scope: shared", result);
        Assert.DoesNotContain("shared`)", result);
        // The remaining single-scope text should be present.
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
    public void Includes_notebook_section_when_hasNotebook_is_true()
    {
        var result = SystemPrompt.Build(hasNotebook: true);

        Assert.Contains("## Notebook", result);
        Assert.Contains("TODO.md", result);
    }

    [Fact]
    public void Omits_notebook_section_when_hasNotebook_is_false()
    {
        var result = SystemPrompt.Build(hasNotebook: false);

        Assert.DoesNotContain("## Notebook", result);
    }

    [Fact]
    public void Includes_library_section_when_enabled()
    {
        var result = SystemPrompt.Build(hasLibrary: true);

        Assert.Contains("## Library", result);
        Assert.Contains("read-only", result);
    }

    [Fact]
    public void Omits_library_section_when_disabled()
    {
        var result = SystemPrompt.Build(hasLibrary: false);

        Assert.DoesNotContain("## Library", result);
    }

    [Fact]
    public void Notes_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasNotes: true);

        Assert.Contains("## Notes", result);
        Assert.Contains("folders", result);
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
    public void Cost_section_always_included()
    {
        var result = SystemPrompt.Build();

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
    public void Chat_section_included_when_enabled()
    {
        var result = SystemPrompt.Build(hasChat: true);

        Assert.Contains("## Agent Chat", result);
        // The single-round 'ask' action must be described...
        Assert.Contains("'ask'", result);
        // ...and the stale multi-turn / early-exit wording must be gone.
        Assert.DoesNotContain("<<DONE>>", result);
        Assert.DoesNotContain("up to 5", result);
    }

    [Fact]
    public void Chat_section_shows_allowed_agents()
    {
        var result = SystemPrompt.Build(hasChat: true, chatAgentNames: ["bob", "alice"]);

        Assert.Contains("bob", result);
        Assert.Contains("alice", result);
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
        Assert.DoesNotContain("## iMessage", result);
        Assert.DoesNotContain("## Health", result);
        Assert.DoesNotContain("## Scheduled Tasks", result);
        Assert.DoesNotContain("## Agent Chat", result);
    }

    // --- Tools section ---

    [Fact]
    public void Tools_section_excluded_when_no_tools()
    {
        var result = SystemPrompt.Build();

        Assert.DoesNotContain("## Tools", result);
    }
}
