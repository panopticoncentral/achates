using System.Text.Json;
using Achates.Server;
using Achates.Server.Mobile;

namespace Achates.Tests;

/// <summary>
/// Pins that agent.update rebuilds AgentConfig WITHOUT dropping capabilities the
/// mobile client doesn't edit (and therefore never sends). The handler rewrites the
/// whole AGENT.md; before the carry-forward, one Save from the app silently stripped
/// <c>**Provider:**</c> and <c>**Memory Budget:**</c> lines from the file.
/// </summary>
public sealed class AgentUpdateCarryForwardTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A typical full payload from the Apple client — no provider/memory_budget keys.</summary>
    private const string ClientPayload = """
        {
            "agent": "maya",
            "description": "Updated description",
            "tools": ["session", "notebook"],
            "allowed_chats": [],
            "prompt": "You are Maya.",
            "model": "anthropic/claude-sonnet-4.6",
            "thinking_model": "",
            "voice": "af_nicole",
            "speech_rate": 0,
            "shared_memory": true
        }
        """;

    [Fact]
    public void Preserves_provider_and_memory_budget_absent_from_params()
    {
        var existing = new AgentConfig
        {
            Title = "Dr. Maya",
            Provider = "anthropic",
            MemoryBudgetTokens = 16_000,
        };

        var updated = MobileTransport.BuildUpdatedAgentConfig(Params(ClientPayload), existing);

        Assert.Equal("anthropic", updated.Provider);
        Assert.Equal(16_000, updated.MemoryBudgetTokens);
        Assert.Equal("Dr. Maya", updated.Title);
    }

    [Fact]
    public void Applies_params_over_existing_values()
    {
        var existing = new AgentConfig
        {
            Description = "Old description",
            Model = "anthropic/claude-opus-4.7",
            ThinkingModel = "anthropic/claude-opus-4.7",
            Voice = null,
        };

        var updated = MobileTransport.BuildUpdatedAgentConfig(Params(ClientPayload), existing);

        Assert.Equal("Updated description", updated.Description);
        Assert.Equal("anthropic/claude-sonnet-4.6", updated.Model);
        Assert.Null(updated.ThinkingModel); // empty string clears the override
        Assert.Equal("af_nicole", updated.Voice);
        Assert.Equal(["session", "notebook"], updated.Tools);
    }

    [Fact]
    public void Handles_no_existing_config()
    {
        var updated = MobileTransport.BuildUpdatedAgentConfig(Params(ClientPayload), existing: null);

        Assert.Null(updated.Provider);
        Assert.Null(updated.MemoryBudgetTokens);
        Assert.Equal("Updated description", updated.Description);
    }
}
