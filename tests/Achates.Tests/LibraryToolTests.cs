using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class LibraryToolTests : IDisposable
{
    private readonly string _root;

    public LibraryToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"achates-library-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private LibraryTool CreateTool(bool supportsFileInput = true) => new(_root, supportsFileInput);

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    private static JsonElement JE(string s) => JsonDocument.Parse($"\"{s}\"").RootElement;

    private static string Text(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<CompletionTextContent>().Select(c => c.Text));

    [Fact]
    public async Task List_ShowsReadableFilesAndDirectories_HidesOthers()
    {
        File.WriteAllText(Path.Combine(_root, "guide.md"), "# Guide");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "notes");
        File.WriteAllBytes(Path.Combine(_root, "manual.pdf"), [0x25, 0x50]);
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), [0x89, 0x50]);
        Directory.CreateDirectory(Path.Combine(_root, "papers"));

        var result = await CreateTool().ExecuteAsync("t1", Args(("action", JE("list"))));
        var text = Text(result);

        Assert.Contains("papers/", text);
        Assert.Contains("guide.md", text);
        Assert.Contains("notes.txt", text);
        Assert.Contains("manual.pdf", text);
        Assert.DoesNotContain("photo.png", text);
    }

    [Fact]
    public async Task Read_Markdown_ReturnsInlinedContents()
    {
        File.WriteAllText(Path.Combine(_root, "guide.md"), "# Guide\nhello world\n");

        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("guide.md"))));

        Assert.Contains("hello world", Text(result));
        Assert.Null(result.InjectedUserContent);
    }

    [Fact]
    public async Task Read_Text_ReturnsInlinedContents()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "plain text body");

        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("notes.txt"))));

        Assert.Contains("plain text body", Text(result));
    }

    [Fact]
    public async Task Read_Pdf_ConfirmsAndInjectsFileBlock()
    {
        File.WriteAllBytes(Path.Combine(_root, "manual.pdf"), [0x25, 0x50, 0x44, 0x46]);

        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("manual.pdf"))));

        Assert.Contains("manual.pdf", Text(result));
        Assert.NotNull(result.InjectedUserContent);
        var file = Assert.IsType<CompletionFileContent>(Assert.Single(result.InjectedUserContent!));
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal("manual.pdf", file.FileName);
        Assert.False(string.IsNullOrEmpty(file.Data));
    }

    [Fact]
    public async Task Read_Pdf_OnNonFileModel_RejectedWithoutInjection()
    {
        File.WriteAllBytes(Path.Combine(_root, "manual.pdf"), [0x25, 0x50, 0x44, 0x46]);

        var result = await CreateTool(supportsFileInput: false).ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("manual.pdf"))));

        Assert.Null(result.InjectedUserContent);
        var text = string.Join("\n", result.Content.OfType<CompletionTextContent>().Select(c => c.Text));
        Assert.DoesNotContain("Loaded", text);
        Assert.Contains("manual.pdf", text);
        // Message should make clear the model can't read PDFs.
        Assert.Contains("PDF", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_DisallowedExtension_Rejected()
    {
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), [0x89]);

        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("photo.png"))));

        Assert.Contains(".pdf", Text(result));
        Assert.Null(result.InjectedUserContent);
    }

    [Fact]
    public async Task Write_Action_Rejected_ReadOnly()
    {
        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("write")), ("path", JE("new.md")), ("content", JE("nope"))));

        Assert.Contains("read-only", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "new.md")));
    }

    [Fact]
    public async Task Read_PathEscape_Rejected()
    {
        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("../outside.md"))));

        Assert.Contains("escapes", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_AbsolutePath_Rejected()
    {
        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("/etc/hosts"))));

        Assert.Contains("Absolute", Text(result));
    }

    [Fact]
    public async Task Read_MissingFile_ReportsClearly()
    {
        var result = await CreateTool().ExecuteAsync("t1", Args(
            ("action", JE("read")), ("path", JE("missing.md"))));

        Assert.Contains("not found", Text(result), StringComparison.OrdinalIgnoreCase);
    }
}
