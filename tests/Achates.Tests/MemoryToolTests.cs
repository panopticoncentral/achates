using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class MemoryToolTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sharedPath;
    private readonly string _agentPath;

    public MemoryToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"achates-memorytool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _sharedPath = Path.Combine(_dir, "shared.md");
        _agentPath = Path.Combine(_dir, "agent.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement;

    private static string Text(AgentToolResult r) =>
        ((CompletionTextContent)r.Content[0]).Text;

    // ---------------- Shared-enabled mode (today's behavior) ----------------

    [Fact]
    public void SchemaExposesBothScopes_WhenSharedEnabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.Contains("\"shared\"", schemaJson);
        Assert.Contains("\"agent\"", schemaJson);
    }

    [Fact]
    public async Task Read_WithoutScope_ReturnsBoth_WhenSharedEnabled()
    {
        await File.WriteAllTextAsync(_sharedPath, "user is Paul");
        await File.WriteAllTextAsync(_agentPath, "campaign log");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        var result = await tool.ExecuteAsync("t", Args(("action", JE("read"))));

        var text = Text(result);
        Assert.Contains("user is Paul", text);
        Assert.Contains("campaign log", text);
    }

    [Fact]
    public async Task Save_WithSharedScope_WritesSharedFile_WhenSharedEnabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("scope", JE("shared")),
            ("content", JE("shared note"))));

        Assert.Equal("shared note", await File.ReadAllTextAsync(_sharedPath));
        Assert.False(File.Exists(_agentPath));
    }

    // ---------------- Shared-disabled mode (the new path) ----------------

    [Fact]
    public void SchemaOmitsScope_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.DoesNotContain("\"shared\"", schemaJson);
        // The whole 'scope' parameter is gone — schema only describes action + content.
        Assert.DoesNotContain("\"scope\"", schemaJson);
    }

    [Fact]
    public async Task Read_WithoutScope_ReturnsOnlyAgent_WhenSharedDisabled()
    {
        await File.WriteAllTextAsync(_sharedPath, "user is Paul");
        await File.WriteAllTextAsync(_agentPath, "campaign log");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(("action", JE("read"))));

        var text = Text(result);
        Assert.Contains("campaign log", text);
        Assert.DoesNotContain("user is Paul", text);
    }

    [Fact]
    public async Task Save_WithoutScope_WritesAgentFile_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("content", JE("agent note"))));

        Assert.Equal("agent note", await File.ReadAllTextAsync(_agentPath));
        Assert.False(File.Exists(_sharedPath));
    }

    [Fact]
    public async Task Save_WithSharedScope_RoutesToAgent_WhenSharedDisabled()
    {
        // Defensive fallthrough: if a hand-crafted call slips through, it must not
        // touch the shared file.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("scope", JE("shared")),
            ("content", JE("intended for shared"))));

        Assert.False(File.Exists(_sharedPath));
        Assert.Equal("intended for shared", await File.ReadAllTextAsync(_agentPath));
    }
}
