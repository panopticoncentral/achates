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

    // ---------------- Incremental writes: append & edit ----------------

    [Fact]
    public void SchemaIncludesAppendAndEdit()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        var schemaJson = tool.Parameters.GetRawText();
        Assert.Contains("\"append\"", schemaJson);
        Assert.Contains("\"edit\"", schemaJson);
    }

    [Fact]
    public async Task Append_AddsToExistingContentWithNewlineBoundary()
    {
        await File.WriteAllTextAsync(_agentPath, "first line");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("append")),
            ("content", JE("second line"))));

        Assert.Equal("first line\nsecond line", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Append_PreservesTrailingNewline_NoDoubleBlank()
    {
        await File.WriteAllTextAsync(_agentPath, "first line\n");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("append")),
            ("content", JE("second line"))));

        Assert.Equal("first line\nsecond line", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Append_ToMissingFile_CreatesIt()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("append")),
            ("content", JE("brand new"))));

        Assert.Equal("brand new", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Edit_ReplacesUniqueText()
    {
        await File.WriteAllTextAsync(_agentPath, "Paul likes tea and coffee");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")),
            ("old", JE("tea and coffee")),
            ("new", JE("espresso"))));

        Assert.Equal("Paul likes espresso", await File.ReadAllTextAsync(_agentPath));
        Assert.Contains("updated", Text(result));
    }

    [Fact]
    public async Task Edit_WithEmptyNew_DeletesText()
    {
        await File.WriteAllTextAsync(_agentPath, "keep this. remove this.");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")),
            ("old", JE(" remove this.")),
            ("new", JE(""))));

        Assert.Equal("keep this.", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Edit_TextNotFound_ReturnsErrorAndLeavesFileUnchanged()
    {
        await File.WriteAllTextAsync(_agentPath, "original content");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")),
            ("old", JE("nonexistent")),
            ("new", JE("x"))));

        Assert.Contains("not found", Text(result));
        Assert.Equal("original content", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Edit_AmbiguousMatch_ReturnsErrorAndLeavesFileUnchanged()
    {
        await File.WriteAllTextAsync(_agentPath, "note note note");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")),
            ("old", JE("note")),
            ("new", JE("x"))));

        Assert.Contains("appears", Text(result));
        Assert.Equal("note note note", await File.ReadAllTextAsync(_agentPath));
    }

    [Fact]
    public async Task Append_RoutesToAgent_WhenSharedDisabled()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("append")),
            ("scope", JE("shared")),
            ("content", JE("should not reach shared"))));

        Assert.False(File.Exists(_sharedPath));
        Assert.Equal("should not reach shared", await File.ReadAllTextAsync(_agentPath));
    }

    // ---------------- Archive tier: file routing ----------------

    private string ArchiveDir => Path.Combine(_dir, "memory");

    [Fact]
    public void SchemaIncludesFileParam()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        Assert.Contains("\"file\"", tool.Parameters.GetRawText());
    }

    [Fact]
    public async Task Save_WithFile_WritesArchiveFile_NotCore()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("file", JE("journal")),
            ("content", JE("night one"))));

        Assert.Equal("night one", await File.ReadAllTextAsync(Path.Combine(ArchiveDir, "journal.md")));
        Assert.False(File.Exists(_agentPath));
    }

    [Fact]
    public async Task Read_WithFile_ReturnsArchiveContent()
    {
        Directory.CreateDirectory(ArchiveDir);
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "history.md"), "old events");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(
            ("action", JE("read")),
            ("file", JE("history"))));

        Assert.Contains("old events", Text(result));
    }

    [Fact]
    public async Task Append_WithFile_AddsToArchiveFile()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(("action", JE("append")), ("file", JE("journal")), ("content", JE("a"))));
        await tool.ExecuteAsync("t", Args(("action", JE("append")), ("file", JE("journal")), ("content", JE("b"))));

        Assert.Equal("a\nb", await File.ReadAllTextAsync(Path.Combine(ArchiveDir, "journal.md")));
    }

    [Fact]
    public async Task Edit_WithFile_ReplacesUniqueTextInArchive()
    {
        Directory.CreateDirectory(ArchiveDir);
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "n.md"), "keep change me end");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")), ("file", JE("n")), ("old", JE("change me")), ("new", JE("done"))));

        Assert.Equal("keep done end", await File.ReadAllTextAsync(Path.Combine(ArchiveDir, "n.md")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("../../secrets")]
    public async Task ArchiveFile_PathTraversal_IsRejected(string badFile)
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var result = await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("file", JE(badFile)), ("content", JE("x"))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(ArchiveDir) && Directory.GetFiles(ArchiveDir).Length > 0);
    }

    [Fact]
    public async Task Read_MissingArchiveFile_ReturnsFriendlyMessage()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var result = await tool.ExecuteAsync("t", Args(("action", JE("read")), ("file", JE("nope"))));
        Assert.Contains("not found", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Archive tier: list action ----------------

    [Fact]
    public void SchemaIncludesListAction()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        Assert.Contains("\"list\"", tool.Parameters.GetRawText());
    }

    [Fact]
    public async Task List_EmptyArchive_ReturnsFriendlyMessage()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var result = await tool.ExecuteAsync("t", Args(("action", JE("list"))));
        Assert.Contains("empty", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ReturnsFilesWithHeadings()
    {
        Directory.CreateDirectory(ArchiveDir);
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "journal.md"), "# Nightly Journal\nday one");
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "history.md"), "# Marriage History\ndetails");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("list")))));

        Assert.Contains("journal", text);
        Assert.Contains("Nightly Journal", text);
        Assert.Contains("history", text);
        Assert.Contains("Marriage History", text);
    }

    // ---------------- Archive tier: search action ----------------

    [Fact]
    public void SchemaIncludesSearchActionAndQuery()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true);
        var json = tool.Parameters.GetRawText();
        Assert.Contains("\"search\"", json);
        Assert.Contains("\"query\"", json);
    }

    [Fact]
    public async Task Search_FindsBodyMatches_CaseInsensitive()
    {
        Directory.CreateDirectory(ArchiveDir);
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "journal.md"),
            "# Journal\nPaul mentioned the kitchen incident\nunrelated line");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("search")), ("query", JE("KITCHEN")))));

        Assert.Contains("journal", text);
        Assert.Contains("kitchen incident", text);
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsFriendlyMessage()
    {
        Directory.CreateDirectory(ArchiveDir);
        await File.WriteAllTextAsync(Path.Combine(ArchiveDir, "journal.md"), "# Journal\nday one");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("search")), ("query", JE("zzzznotfound")))));

        Assert.Contains("No matches", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_MissingQuery_ReturnsError()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("search")))));
        Assert.Contains("query", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Core soft-budget note ----------------

    [Fact]
    public async Task Save_OverBudget_AppendsNote()
    {
        // Budget of 5 tokens ≈ 20 chars; write more than that.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 5);

        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")),
            ("content", JE(new string('x', 200))))));

        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_UnderBudget_NoNote()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 100_000);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("save")), ("content", JE("tiny")))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_BudgetZero_NoNote()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 0);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("content", JE(new string('x', 200))))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Append_OverBudget_AppendsNote()
    {
        await File.WriteAllTextAsync(_agentPath, new string('x', 180));
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("append")), ("content", JE("more")))));
        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(16000, 8000, 16000)]
    [InlineData(null, 8000, 8000)]
    [InlineData(null, null, MemoryTool.DefaultCoreBudgetTokens)]
    public void ResolveCoreBudgetTokens_FollowsResolutionOrder(int? perAgent, int? globalDefault, int expected)
    {
        Assert.Equal(expected, MemoryTool.ResolveCoreBudgetTokens(perAgent, globalDefault));
    }

    [Fact]
    public async Task Append_WithFile_ToNewFile_SaysAppended()
    {
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("append")), ("file", JE("fresh")), ("content", JE("first")))));
        Assert.Contains("Appended", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(ArchiveDir, "fresh.md")));
    }

    [Fact]
    public async Task Save_SharedScope_OverBudget_NoNote()
    {
        // Budget applies to the agent core only — shared writes never get the nudge.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("scope", JE("shared")), ("content", JE(new string('x', 200))))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_ArchiveFile_OverBudget_NoNote()
    {
        // The budget nudge is for core memory only — archive writes never get it.
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("save")), ("file", JE("big")), ("content", JE(new string('x', 200))))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Budget note also surfaces on core read and edit (so dreamtime, which
    //      reads core up front and maintains it via edit, reliably sees the signal) ----

    [Fact]
    public async Task Read_AgentScope_OverBudget_AppendsNote()
    {
        await File.WriteAllTextAsync(_agentPath, new string('x', 200));
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("read")), ("scope", JE("agent")))));
        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_AgentScope_UnderBudget_NoNote()
    {
        await File.WriteAllTextAsync(_agentPath, "tiny");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 100_000);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("read")), ("scope", JE("agent")))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_SharedScope_OverBudget_NoNote()
    {
        // The budget is the agent core's — a shared-scope read never carries the nudge.
        await File.WriteAllTextAsync(_sharedPath, new string('x', 200));
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("read")), ("scope", JE("shared")))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_Unscoped_AgentOverBudget_AppendsNote()
    {
        // Unscoped read (shared-enabled) returns both files; the note fires on the agent core.
        await File.WriteAllTextAsync(_agentPath, new string('x', 200));
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: true, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(("action", JE("read")))));
        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_AgentScope_OverBudget_AppendsNote()
    {
        await File.WriteAllTextAsync(_agentPath, "keep " + new string('x', 200) + " zzz");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 5);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")), ("old", JE("zzz")), ("new", JE("yyy")))));
        Assert.Contains("updated", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("over the", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_AgentScope_UnderBudget_NoNote()
    {
        await File.WriteAllTextAsync(_agentPath, "Paul likes tea");
        var tool = new MemoryTool(_sharedPath, _agentPath, sharedEnabled: false, coreBudgetTokens: 100_000);
        var text = Text(await tool.ExecuteAsync("t", Args(
            ("action", JE("edit")), ("old", JE("tea")), ("new", JE("coffee")))));
        Assert.DoesNotContain("over the", text, StringComparison.OrdinalIgnoreCase);
    }
}
