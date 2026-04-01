using System.Text.Json;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class AgentCreatorToolTests : IDisposable
{
    private readonly string _agentsDir;
    private readonly List<string> _loadedAgents = [];

    public AgentCreatorToolTests()
    {
        _agentsDir = Path.Combine(Path.GetTempPath(), $"achates-creator-test-{Guid.NewGuid():N}", "agents");
        Directory.CreateDirectory(_agentsDir);
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_agentsDir)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, true);
    }

    private AgentCreatorTool CreateTool() =>
        new(_agentsDir, (name, ct) =>
        {
            _loadedAgents.Add(name);
            return Task.CompletedTask;
        });

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) => JsonDocument.Parse($"\"{s}\"").RootElement;
    private static JsonElement JEArray(params string[] items)
    {
        var json = "[" + string.Join(",", items.Select(i => $"\"{i}\"")) + "]";
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task Create_WritesAgentFileAndCallsLoad()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args(
            ("name", JE("Test Bot")),
            ("description", JE("A test bot.")),
            ("prompt", JE("You are a test bot."))));

        var text = result.Content[0] as Achates.Providers.Completions.Content.CompletionTextContent;
        Assert.Contains("created", text!.Text, StringComparison.OrdinalIgnoreCase);

        // Verify file was written
        var agentDir = Path.Combine(_agentsDir, "test-bot");
        Assert.True(File.Exists(Path.Combine(agentDir, "AGENT.md")));

        // Verify load was called
        Assert.Contains("test-bot", _loadedAgents);

        // Verify content
        var content = await File.ReadAllTextAsync(Path.Combine(agentDir, "AGENT.md"));
        Assert.Contains("# Test Bot", content);
        Assert.Contains("A test bot.", content);
        Assert.Contains("You are a test bot.", content);
    }

    [Fact]
    public async Task Create_WithModelAndTools_IncludesThemInAgentFile()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc2", Args(
            ("name", JE("Tooled Bot")),
            ("description", JE("Has tools.")),
            ("prompt", JE("You have tools.")),
            ("model", JE("anthropic/claude-sonnet-4")),
            ("tools", JEArray("session", "memory"))));

        var agentDir = Path.Combine(_agentsDir, "tooled-bot");
        var content = await File.ReadAllTextAsync(Path.Combine(agentDir, "AGENT.md"));
        Assert.Contains("anthropic/claude-sonnet-4", content);
        Assert.Contains("session", content);
        Assert.Contains("memory", content);
    }

    [Fact]
    public async Task Create_RejectsExistingAgent()
    {
        var tool = CreateTool();
        // Create first
        await tool.ExecuteAsync("tc3a", Args(
            ("name", JE("Duplicate")),
            ("description", JE("First.")),
            ("prompt", JE("First prompt."))));

        // Try to create again
        var result = await tool.ExecuteAsync("tc3b", Args(
            ("name", JE("Duplicate")),
            ("description", JE("Second.")),
            ("prompt", JE("Second prompt."))));

        var text = result.Content[0] as Achates.Providers.Completions.Content.CompletionTextContent;
        Assert.Contains("already exists", text!.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RejectsInvalidName()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc4", Args(
            ("name", JE("!!!")),
            ("description", JE("Bad name.")),
            ("prompt", JE("Prompt."))));

        var text = result.Content[0] as Achates.Providers.Completions.Content.CompletionTextContent;
        Assert.Contains("invalid", text!.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RequiresNameDescriptionPrompt()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc5", Args(
            ("name", JE("Bot"))));

        var text = result.Content[0] as Achates.Providers.Completions.Content.CompletionTextContent;
        Assert.Contains("required", text!.Text, StringComparison.OrdinalIgnoreCase);
    }
}
