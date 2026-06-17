using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Read-only, user-curated collection of reference documents. The agent can
/// <c>list</c> and <c>read</c> files inside a configured root, but never write.
/// Markdown and text files inline as text; PDFs are delivered as a follow-up
/// user-message file block via <see cref="AgentToolResult.InjectedUserContent"/>,
/// because the provider cannot serialize a file inside a tool-result message.
/// </summary>
internal sealed class LibraryTool : AgentTool
{
    private readonly string _root;
    private readonly bool _supportsFileInput;

    public LibraryTool(string root, bool supportsFileInput)
    {
        var full = Path.GetFullPath(root);
        if (full.Length > 1)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _root = full;
        _supportsFileInput = supportsFileInput;
    }

    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["list", "read"], "Action to perform.", "list"),
            ["path"] = StringSchema("Path relative to the library root. For 'list' this is a directory; for 'read' this is a .md, text, or .pdf file. Defaults to the root for 'list'."),
        },
        required: ["action"]);

    public override string Name => "library";
    public override string Description =>
        "A read-only collection of reference documents the user has curated. Use 'list' to browse folders and 'read' to open a document (.md, text, or .pdf). You cannot add, change, or delete anything.";
    public override string Label => "Library";
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
            _ => TextResult($"Unknown action: {action}. The library is read-only; only 'list' and 'read' are supported."),
        });
    }

    private AgentToolResult List(string? relPath)
    {
        if (!TryResolve(relPath, out var abs, out var error))
            return TextResult(error);
        if (!Directory.Exists(abs))
            return TextResult($"Not a directory: {Rel(abs)}");

        var dirs = Directory.EnumerateDirectories(abs)
            .Select(d => Path.GetFileName(d) + "/")
            .OrderBy(s => s, StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(abs)
            .Where(f => IsReadable(f))
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
        if (!TryResolve(relPath, out var abs, out var error))
            return TextResult(error);
        if (!IsReadable(relPath))
            return TextResult($"Only .md, text, or .pdf files can be read. Got: {relPath}");
        if (!File.Exists(abs))
            return TextResult($"File not found: {Rel(abs)}");

        var name = Path.GetFileName(abs);

        if (IsPdf(abs))
        {
            if (!_supportsFileInput)
                return TextResult($"\"{name}\" is a PDF, but this agent's model can't read PDF files. Markdown and text documents still work.");

            var data = Convert.ToBase64String(File.ReadAllBytes(abs));
            return new AgentToolResult
            {
                Content = [new CompletionTextContent
                {
                    Text = $"Loaded \"{name}\" from the library into the conversation below.",
                }],
                InjectedUserContent =
                [
                    new CompletionFileContent
                    {
                        Data = data,
                        MimeType = "application/pdf",
                        FileName = name,
                    },
                ],
            };
        }

        var contents = File.ReadAllText(abs);
        var lang = FenceLanguage(name);
        return TextResult($"Contents of \"{name}\" from the library:\n\n```{lang}\n{contents}\n```");
    }

    private static bool IsReadable(string path) =>
        IsMarkdown(path) || IsText(path) || IsPdf(path);

    private static bool IsMarkdown(string path) =>
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    private static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".txt" or ".text" or ".csv" or ".tsv" or ".json" or ".xml"
            or ".yaml" or ".yml" or ".html" or ".htm" or ".markdown" or ".log";
    }

    private static string FenceLanguage(string fileName) =>
        Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() switch
        {
            "csv" => "csv",
            "tsv" => "tsv",
            "json" => "json",
            "xml" => "xml",
            "md" or "markdown" => "markdown",
            "yaml" or "yml" => "yaml",
            "html" or "htm" => "html",
            _ => "",
        };

    private bool TryResolve(string? relPath, out string absPath, out string error)
    {
        absPath = "";
        error = "";

        var trimmed = relPath?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed is "." or "./" or ".\\" or "/" or "\\")
        {
            absPath = _root;
            return true;
        }

        if (Path.IsPathRooted(trimmed))
        {
            error = "Absolute paths are not allowed. Use a path relative to the library root.";
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(_root, trimmed));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (combined != _root && !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            error = $"Path '{relPath}' escapes the library root.";
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
