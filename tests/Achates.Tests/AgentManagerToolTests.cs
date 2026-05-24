using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class AgentManagerToolTests : IDisposable
{
    private readonly string _agentsDir;
    private readonly List<string> _loadedAgents = [];
    private readonly List<(string oldId, string newId, string display)> _renames = [];

    public AgentManagerToolTests()
    {
        _agentsDir = Path.Combine(Path.GetTempPath(), $"achates-manager-test-{Guid.NewGuid():N}", "agents");
        Directory.CreateDirectory(_agentsDir);
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_agentsDir)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, true);
    }

    private AgentManagerTool CreateTool() =>
        new(_agentsDir,
            (name, ct) =>
            {
                _loadedAgents.Add(name);
                return Task.CompletedTask;
            },
            (oldId, newId, display, ct) =>
            {
                _renames.Add((oldId, newId, display));
                return Task.CompletedTask;
            });

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement;
    private static JsonElement JEArray(params string[] items) =>
        JsonDocument.Parse(JsonSerializer.Serialize(items)).RootElement;

    private static string Text(AgentToolResult r) =>
        ((CompletionTextContent)r.Content[0]).Text;

    private async Task SeedAgentAsync(string displayName, string description, string prompt, string[]? tools = null)
    {
        var tool = CreateTool();
        var args = new List<(string, object?)>
        {
            ("action", JE("create")),
            ("name", JE(displayName)),
            ("description", JE(description)),
            ("prompt", JE(prompt)),
        };
        if (tools is not null) args.Add(("tools", JEArray(tools)));
        await tool.ExecuteAsync("seed", Args(args.ToArray()));
    }

    [Fact]
    public async Task Create_WritesAgentFileAndCallsLoad()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc1", Args(
            ("action", JE("create")),
            ("name", JE("Test Bot")),
            ("description", JE("A test bot.")),
            ("prompt", JE("You are a test bot."))));

        Assert.Contains("created", Text(result), StringComparison.OrdinalIgnoreCase);

        var agentDir = Path.Combine(_agentsDir, "test-bot");
        Assert.True(File.Exists(Path.Combine(agentDir, "AGENT.md")));
        Assert.Contains("test-bot", _loadedAgents);

        var content = await File.ReadAllTextAsync(Path.Combine(agentDir, "AGENT.md"));
        Assert.Contains("# Test Bot", content);
        Assert.Contains("A test bot.", content);
        Assert.Contains("You are a test bot.", content);
    }

    [Fact]
    public async Task Create_WithTools_IncludesThemInAgentFile()
    {
        var tool = CreateTool();
        await tool.ExecuteAsync("tc2", Args(
            ("action", JE("create")),
            ("name", JE("Tooled Bot")),
            ("description", JE("Has tools.")),
            ("prompt", JE("You have tools.")),
            ("tools", JEArray("session", "memory"))));

        var content = await File.ReadAllTextAsync(Path.Combine(_agentsDir, "tooled-bot", "AGENT.md"));
        Assert.Contains("session", content);
        Assert.Contains("memory", content);
    }

    [Fact]
    public async Task Create_RejectsExistingAgent()
    {
        var tool = CreateTool();
        await tool.ExecuteAsync("tc3a", Args(
            ("action", JE("create")),
            ("name", JE("Duplicate")),
            ("description", JE("First.")),
            ("prompt", JE("First prompt."))));

        var result = await tool.ExecuteAsync("tc3b", Args(
            ("action", JE("create")),
            ("name", JE("Duplicate")),
            ("description", JE("Second.")),
            ("prompt", JE("Second prompt."))));

        Assert.Contains("already exists", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RejectsInvalidName()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc4", Args(
            ("action", JE("create")),
            ("name", JE("!!!")),
            ("description", JE("Bad name.")),
            ("prompt", JE("Prompt."))));

        Assert.Contains("invalid", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RequiresNameDescriptionPrompt()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("tc5", Args(
            ("action", JE("create")),
            ("name", JE("Bot"))));

        Assert.Contains("required", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ReturnsCreatedAgents()
    {
        await SeedAgentAsync("Alpha", "First agent.", "Alpha prompt.");
        await SeedAgentAsync("Beta", "Second agent.", "Beta prompt.");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("l1", Args(("action", JE("list"))));
        var text = Text(result);

        Assert.Contains("alpha", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("First agent.", text);
        Assert.Contains("beta", text);
    }

    [Fact]
    public async Task Read_ReturnsDefinition()
    {
        await SeedAgentAsync("Reader", "Reads things.", "You read.", ["session", "memory"]);

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("r1", Args(
            ("action", JE("read")),
            ("agent", JE("reader"))));
        var text = Text(result);

        Assert.Contains("Reads things.", text);
        Assert.Contains("You read.", text);
        Assert.Contains("session", text);
    }

    [Fact]
    public async Task Read_UnknownAgent_Errors()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("r2", Args(
            ("action", JE("read")),
            ("agent", JE("nope"))));

        Assert.Contains("not found", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Modify_ChangesFieldAndReloads()
    {
        await SeedAgentAsync("Editable", "Old description.", "Old prompt.");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("m1", Args(
            ("action", JE("modify")),
            ("agent", JE("editable")),
            ("description", JE("New description.")),
            ("tools", JEArray("session"))));

        Assert.Contains("updated", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("editable", _loadedAgents);
        Assert.Empty(_renames);

        var content = await File.ReadAllTextAsync(Path.Combine(_agentsDir, "editable", "AGENT.md"));
        Assert.Contains("New description.", content);
        Assert.DoesNotContain("Old description.", content);
        Assert.Contains("session", content);
    }

    [Fact]
    public async Task Modify_NoFields_Errors()
    {
        await SeedAgentAsync("Bare", "Desc.", "Prompt.");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("m2", Args(
            ("action", JE("modify")),
            ("agent", JE("bare"))));

        Assert.Contains("at least one", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Modify_RenamesWhenNameNormalizesToNewId()
    {
        await SeedAgentAsync("Old Name", "Desc.", "Prompt.");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("m3", Args(
            ("action", JE("modify")),
            ("agent", JE("old-name")),
            ("name", JE("Brand New")),
            ("description", JE("Updated."))));

        Assert.Contains("Renamed", Text(result));
        var rename = Assert.Single(_renames);
        Assert.Equal("old-name", rename.oldId);
        Assert.Equal("brand-new", rename.newId);
        Assert.Equal("Brand New", rename.display);
        // Field edits land in the original dir before rename is delegated.
        var content = await File.ReadAllTextAsync(Path.Combine(_agentsDir, "old-name", "AGENT.md"));
        Assert.Contains("# Brand New", content);
        Assert.Contains("Updated.", content);
    }

    [Fact]
    public async Task Modify_RejectsRenameToExistingId()
    {
        await SeedAgentAsync("Taken", "Desc.", "Prompt.");
        await SeedAgentAsync("Mover", "Desc.", "Prompt.");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("m4", Args(
            ("action", JE("modify")),
            ("agent", JE("mover")),
            ("name", JE("Taken"))));

        Assert.Contains("already exists", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_renames);
    }

    [Fact]
    public async Task Modify_UnknownAgent_Errors()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("m5", Args(
            ("action", JE("modify")),
            ("agent", JE("ghost")),
            ("description", JE("x"))));

        Assert.Contains("not found", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Modify_SetsSharedMemoryFalse_PersistsToAgentFile()
    {
        await SeedAgentAsync("Roleplay Bot", "A DM.", "You are a DM.");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("m1", Args(
            ("action", JE("modify")),
            ("agent", JE("roleplay-bot")),
            ("shared_memory", JsonDocument.Parse("false").RootElement)));

        Assert.Contains("updated", Text(result), StringComparison.OrdinalIgnoreCase);
        var content = await File.ReadAllTextAsync(
            Path.Combine(_agentsDir, "roleplay-bot", "AGENT.md"));
        Assert.Contains("**Shared Memory:** false", content);
    }

    [Fact]
    public async Task Modify_SetsSharedMemoryTrue_OmitsLineFromAgentFile()
    {
        // Start with shared_memory: false so we can verify flipping it back removes the line.
        await SeedAgentAsync("Roleplay Bot", "A DM.", "You are a DM.");
        var tool = CreateTool();
        await tool.ExecuteAsync("m1", Args(
            ("action", JE("modify")),
            ("agent", JE("roleplay-bot")),
            ("shared_memory", JsonDocument.Parse("false").RootElement)));

        await tool.ExecuteAsync("m2", Args(
            ("action", JE("modify")),
            ("agent", JE("roleplay-bot")),
            ("shared_memory", JsonDocument.Parse("true").RootElement)));

        var content = await File.ReadAllTextAsync(
            Path.Combine(_agentsDir, "roleplay-bot", "AGENT.md"));
        Assert.DoesNotContain("Shared Memory", content);
    }
}
