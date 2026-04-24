using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class NotebookToolTests : IDisposable
{
    private readonly string _root;

    public NotebookToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"achates-notebook-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private NotebookTool CreateTool() => new(_root);

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) => JsonDocument.Parse($"\"{s}\"").RootElement;

    private static string Text(AgentToolResult result) =>
        ((CompletionTextContent)result.Content[0]).Text;

    [Fact]
    public async Task List_EmptyRoot_ReportsEmpty()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(("action", JE("list"))));

        Assert.Contains("empty", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ShowsMarkdownFilesAndDirectories_HidesOtherFiles()
    {
        File.WriteAllText(Path.Combine(_root, "todo.md"), "# Todo");
        File.WriteAllText(Path.Combine(_root, "ideas.md"), "# Ideas");
        File.WriteAllText(Path.Combine(_root, "note.txt"), "not markdown");
        Directory.CreateDirectory(Path.Combine(_root, "drafts"));

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(("action", JE("list"))));

        var text = Text(result);
        Assert.Contains("drafts/", text);
        Assert.Contains("ideas.md", text);
        Assert.Contains("todo.md", text);
        Assert.DoesNotContain("note.txt", text);
    }

    [Fact]
    public async Task List_NestedPath_ListsSubdirectoryContents()
    {
        Directory.CreateDirectory(Path.Combine(_root, "drafts"));
        File.WriteAllText(Path.Combine(_root, "drafts", "post.md"), "draft");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("list")),
            ("path", JE("drafts"))));

        Assert.Contains("post.md", Text(result));
    }

    [Fact]
    public async Task List_PathEscape_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("list")),
            ("path", JE("../.."))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_SiblingPrefixEscape_Rejected()
    {
        // A path that resolves to a sibling directory whose name shares a prefix
        // with the root (e.g. root=/tmp/notebook, target=/tmp/notebook-evil).
        // The rootWithSep guard in TryResolve must not treat this as a descendant.
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var tool = CreateTool();
            var result = await tool.ExecuteAsync("t1", Args(
                ("action", JE("list")),
                ("path", JE($"../{Path.GetFileName(sibling)}"))));

            Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task List_AbsolutePath_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("list")),
            ("path", JE("/etc"))));

        Assert.Contains("Absolute", Text(result));
    }

    [Fact]
    public async Task List_MissingDirectory_ReportsClearly()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("list")),
            ("path", JE("does-not-exist"))));

        Assert.Contains("Not a directory", Text(result));
    }

    [Fact]
    public async Task Read_ReturnsFileContents()
    {
        File.WriteAllText(Path.Combine(_root, "todo.md"), "# Todo\n- buy milk\n");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("read")),
            ("path", JE("todo.md"))));

        Assert.Contains("buy milk", Text(result));
    }

    [Fact]
    public async Task Read_NonMarkdownExtension_Rejected()
    {
        File.WriteAllText(Path.Combine(_root, "secrets.txt"), "shhh");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("read")),
            ("path", JE("secrets.txt"))));

        Assert.Contains(".md", Text(result));
        Assert.DoesNotContain("shhh", Text(result));
    }

    [Fact]
    public async Task Read_MissingFile_ReportsClearly()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("read")),
            ("path", JE("missing.md"))));

        Assert.Contains("not found", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_PathEscape_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("read")),
            ("path", JE("../outside.md"))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_CreatesFile()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("new.md")),
            ("content", JE("# Hello"))));

        Assert.Contains("wrote", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# Hello", File.ReadAllText(Path.Combine(_root, "new.md")));
    }

    [Fact]
    public async Task Write_ReplacesExistingFile()
    {
        File.WriteAllText(Path.Combine(_root, "todo.md"), "old");

        var tool = CreateTool();
        await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("todo.md")),
            ("content", JE("new"))));

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "todo.md")));
    }

    [Fact]
    public async Task Write_AutoCreatesParentDirectories()
    {
        var tool = CreateTool();
        await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("drafts/2026/post.md")),
            ("content", JE("draft"))));

        Assert.Equal("draft", File.ReadAllText(Path.Combine(_root, "drafts", "2026", "post.md")));
    }

    [Fact]
    public async Task Write_NonMarkdownExtension_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("oops.txt")),
            ("content", JE("nope"))));

        Assert.Contains(".md", Text(result));
        Assert.False(File.Exists(Path.Combine(_root, "oops.txt")));
    }

    [Fact]
    public async Task Write_PathEscape_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("../outside.md")),
            ("content", JE("leak"))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_MissingContent_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("write")),
            ("path", JE("new.md"))));

        Assert.Contains("content", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mkdir_CreatesDirectory()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("mkdir")),
            ("path", JE("drafts"))));

        Assert.Contains("drafts", Text(result));
        Assert.True(Directory.Exists(Path.Combine(_root, "drafts")));
    }

    [Fact]
    public async Task Mkdir_NestedPath_CreatesParents()
    {
        var tool = CreateTool();
        await tool.ExecuteAsync("t1", Args(
            ("action", JE("mkdir")),
            ("path", JE("a/b/c"))));

        Assert.True(Directory.Exists(Path.Combine(_root, "a", "b", "c")));
    }

    [Fact]
    public async Task Mkdir_AlreadyExists_IsNoOp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "drafts"));

        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("mkdir")),
            ("path", JE("drafts"))));

        Assert.DoesNotContain("error", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mkdir_PathEscape_Rejected()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("t1", Args(
            ("action", JE("mkdir")),
            ("path", JE("../outside"))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
    }
}
