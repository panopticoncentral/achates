using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// User-facing markdown workspace. The agent can list, read, write, and create
/// directories inside a configured root. Reads and writes are restricted to
/// <c>.md</c> files. All paths are resolved against the root and must stay
/// inside it.
/// </summary>
internal sealed class NotebookTool : AgentTool
{
    private readonly string _root;

    public NotebookTool(string root)
    {
        _root = Path.GetFullPath(root);
    }

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read", "write", "mkdir"], "Action to perform.", "list"),
            ["path"] = StringSchema("Path relative to the notebook root. For 'list' and 'mkdir' this is a directory; for 'read' and 'write' this is a .md file. Defaults to the root for 'list'."),
            ["content"] = StringSchema("File content. Required for 'write' — replaces the entire file."),
        },
        required: ["action"]);

    public override string Name => "notebook";
    public override string Description =>
        "A folder of markdown files for long-term notes, todos, drafts, and ideas. Use 'list' to see what's there, 'read' to open a .md file, 'write' to create or replace a .md file (replaces the whole file), and 'mkdir' to make a subfolder.";
    public override string Label => "Notebook";
    public override JsonElement Parameters => _schema;

    public override Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "list";
        var path = GetString(arguments, "path");

        return Task.FromResult<AgentToolResult>(action switch
        {
            "list" => List(path),
            "read" => Read(path),
            "write" => Write(path, GetString(arguments, "content")),
            "mkdir" => Mkdir(path),
            _ => TextResult($"Unknown action: {action}"),
        });
    }

    private AgentToolResult List(string? relPath)
    {
        if (!TryResolve(relPath ?? ".", out var abs, out var error))
            return TextResult(error);

        if (!Directory.Exists(abs))
            return TextResult($"Not a directory: {Rel(abs)}");

        var dirs = Directory.EnumerateDirectories(abs)
            .Select(d => Path.GetFileName(d) + "/")
            .OrderBy(s => s, StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(abs)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f))
            .OrderBy(s => s, StringComparer.Ordinal);
        var entries = dirs.Concat(files).ToList();

        if (entries.Count == 0)
            return TextResult($"{Rel(abs)} is empty.");

        return TextResult($"Contents of {Rel(abs)}:\n- {string.Join("\n- ", entries)}");
    }

    private AgentToolResult Read(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return TextResult("path is required for 'read'.");
        if (!IsMarkdown(relPath))
            return TextResult($"Only .md files can be read. Got: {relPath}");
        if (!TryResolve(relPath, out var abs, out var error))
            return TextResult(error);
        if (!File.Exists(abs))
            return TextResult($"File not found: {Rel(abs)}");

        var content = File.ReadAllText(abs);
        return TextResult(content);
    }

    private AgentToolResult Write(string? relPath, string? content)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return TextResult("path is required for 'write'.");
        if (!IsMarkdown(relPath))
            return TextResult($"Only .md files can be written. Got: {relPath}");
        if (content is null)
            return TextResult("content is required for 'write'.");
        if (!TryResolve(relPath, out var abs, out var error))
            return TextResult(error);

        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(abs, content);
        return TextResult($"Wrote {Rel(abs)} ({content.Length} chars).");
    }

    private AgentToolResult Mkdir(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return TextResult("path is required for 'mkdir'.");
        if (!TryResolve(relPath, out var abs, out var error))
            return TextResult(error);

        Directory.CreateDirectory(abs);
        return TextResult($"Created directory {Rel(abs)}.");
    }

    private static bool IsMarkdown(string path) =>
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    private bool TryResolve(string relPath, out string absPath, out string error)
    {
        absPath = "";
        error = "";

        if (Path.IsPathRooted(relPath))
        {
            error = "Absolute paths are not allowed. Use a path relative to the notebook root.";
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(_root, relPath));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (combined != _root && !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            error = $"Path '{relPath}' escapes the notebook root.";
            return false;
        }

        absPath = combined;
        return true;
    }

    private string Rel(string absPath)
    {
        if (absPath == _root) return ".";
        var rel = Path.GetRelativePath(_root, absPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
