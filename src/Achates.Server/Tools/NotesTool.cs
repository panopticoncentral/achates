using System.Diagnostics;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Access to Apple Notes across all accounts and folders.
/// Supports listing folders, listing notes, reading, creating,
/// renaming, and replacing note contents.
/// </summary>
internal sealed class NotesTool : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(["folders", "list", "read", "create", "update", "rename"], "Action to perform.", "list"),
            ["folder"] = StringSchema("Folder name. Required for 'list', 'read', 'create', 'update', and 'rename'."),
            ["title"] = StringSchema("Exact note title. Required for 'read', 'update', and 'rename'."),
            ["new_title"] = StringSchema("New exact note title. Required for 'create' and 'rename'."),
            ["content"] = StringSchema("Full note content as HTML. Use standard tags: <h1>, <b>, <i>, <ul>/<ol>/<li>, <br>, etc. Required for 'create' and 'update'."),
        },
        required: ["action"]);

    public override string Name => "notes";
    public override string Description => "Access Apple Notes across all folders and accounts. Use 'folders' to discover available folders, then specify a folder for other actions.";
    public override string Label => "Notes";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "list";
        var folder = GetString(arguments, "folder");

        return action switch
        {
            "folders" => await ListFoldersAsync(cancellationToken),
            "list" => await ListAsync(folder, cancellationToken),
            "read" => await ReadAsync(folder, GetString(arguments, "title"), cancellationToken),
            "create" => await CreateAsync(folder, GetString(arguments, "new_title"), GetString(arguments, "content"), cancellationToken),
            "update" => await UpdateAsync(folder, GetString(arguments, "title"), GetString(arguments, "content"), cancellationToken),
            "rename" => await RenameAsync(folder, GetString(arguments, "title"), GetString(arguments, "new_title"), cancellationToken),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListFoldersAsync(CancellationToken cancellationToken)
    {
        var result = await RunScriptAsync(BuildListFoldersScript(), cancellationToken);
        if (!result.Success)
            return TextResult(result.Message);

        var folders = ParseLines(result.Message);
        if (folders.Count == 0)
            return TextResult("No folders found in Apple Notes.");

        return TextResult($"Folders:\n- {string.Join("\n- ", folders)}");
    }

    private async Task<AgentToolResult> ListAsync(string? folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return TextResult("folder is required for 'list'.");

        var result = await RunScriptAsync(BuildListScript(folder), cancellationToken);
        if (!result.Success)
            return TextResult(result.Message);

        var titles = ParseLines(result.Message);
        if (titles.Count == 0)
            return TextResult($"No notes found in '{folder}'.");

        return TextResult($"Notes in '{folder}':\n- {string.Join("\n- ", titles)}");
    }

    private async Task<AgentToolResult> ReadAsync(string? folder, string? title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return TextResult("folder is required for 'read'.");
        if (string.IsNullOrWhiteSpace(title))
            return TextResult("title is required for 'read'.");

        var result = await RunScriptAsync(BuildReadScript(folder, title), cancellationToken);
        return TextResult(result.Message);
    }

    private async Task<AgentToolResult> CreateAsync(string? folder, string? title, string? content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return TextResult("folder is required for 'create'.");
        if (string.IsNullOrWhiteSpace(title))
            return TextResult("new_title is required for 'create'.");
        if (content is null)
            return TextResult("content is required for 'create'.");

        var result = await RunScriptAsync(BuildCreateScript(folder, title, content), cancellationToken);
        return TextResult(result.Message);
    }

    private async Task<AgentToolResult> UpdateAsync(string? folder, string? title, string? content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return TextResult("folder is required for 'update'.");
        if (string.IsNullOrWhiteSpace(title))
            return TextResult("title is required for 'update'.");
        if (content is null)
            return TextResult("content is required for 'update'.");

        var result = await RunScriptAsync(BuildUpdateScript(folder, title, content), cancellationToken);
        return TextResult(result.Message);
    }

    private async Task<AgentToolResult> RenameAsync(string? folder, string? title, string? newTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return TextResult("folder is required for 'rename'.");
        if (string.IsNullOrWhiteSpace(title))
            return TextResult("title is required for 'rename'.");
        if (string.IsNullOrWhiteSpace(newTitle))
            return TextResult("new_title is required for 'rename'.");

        var result = await RunScriptAsync(BuildRenameScript(folder, title, newTitle), cancellationToken);
        return TextResult(result.Message);
    }

    private async Task<ScriptResult> RunScriptAsync(string script, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            return new ScriptResult(false, "The notes tool is only available on macOS.");

        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(stderr) ? "Apple Notes command failed." : stderr;
            return new ScriptResult(false, error);
        }

        return ParseResult(stdout);
    }

    private ScriptResult ParseResult(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new ScriptResult(false, "Apple Notes returned no data.");

        if (output.StartsWith("OK:", StringComparison.Ordinal))
            return new ScriptResult(true, output[3..].TrimStart());

        if (output.StartsWith("ERROR:", StringComparison.Ordinal))
            return new ScriptResult(false, output[6..].TrimStart());

        return new ScriptResult(true, output);
    }

    // --- AppleScript builders ---

    private static string BuildListFoldersScript() => """
tell application "Notes"
    set folderLines to {}
    repeat with accountRef in accounts
        set accountName to name of accountRef
        repeat with folderRef in folders of accountRef
            copy (accountName & " / " & name of folderRef) to end of folderLines
        end repeat
    end repeat

    if (count of folderLines) is 0 then
        return "OK:"
    end if

    set AppleScript's text item delimiters to linefeed
    set joined to folderLines as text
    set AppleScript's text item delimiters to ""
    return "OK:" & joined
end tell
""";

    private static string FindFolderPreamble(string folder) => $$"""
    set matchedFolders to {}
    repeat with accountRef in accounts
        repeat with folderRef in folders of accountRef
            if name of folderRef is "{{EscapeAppleScriptString(folder)}}" then
                copy folderRef to end of matchedFolders
            end if
        end repeat
    end repeat

    if (count of matchedFolders) is 0 then
        return "ERROR: Folder '{{EscapeAppleScriptString(folder)}}' was not found in Apple Notes."
    end if

    if (count of matchedFolders) is greater than 1 then
        return "ERROR: Folder '{{EscapeAppleScriptString(folder)}}' exists in multiple Notes accounts. Rename it or keep only one copy."
    end if

    set folderRef to item 1 of matchedFolders
""";

    private static string BuildListScript(string folder) =>
        $$"""
tell application "Notes"
{{FindFolderPreamble(folder)}}
    set titleLines to {}
    repeat with noteRef in notes of folderRef
        copy name of noteRef to end of titleLines
    end repeat

    if (count of titleLines) is 0 then
        return "OK:"
    end if

    set AppleScript's text item delimiters to linefeed
    set joinedTitles to titleLines as text
    set AppleScript's text item delimiters to ""
    return "OK:" & joinedTitles
end tell
""";

    private static string BuildReadScript(string folder, string title) =>
        $$"""
tell application "Notes"
{{FindFolderPreamble(folder)}}
    set matchingNotes to every note of folderRef whose name is "{{EscapeAppleScriptString(title)}}"

    if (count of matchingNotes) is 0 then
        return "ERROR: Note '{{EscapeAppleScriptString(title)}}' was not found in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    if (count of matchingNotes) is greater than 1 then
        return "ERROR: Multiple notes named '{{EscapeAppleScriptString(title)}}' exist in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    set noteRef to item 1 of matchingNotes
    return "OK:" & "Title: " & (name of noteRef) & linefeed & linefeed & (body of noteRef)
end tell
""";

    private static string BuildCreateScript(string folder, string title, string content) =>
        $$"""
tell application "Notes"
{{FindFolderPreamble(folder)}}
    set existingNotes to every note of folderRef whose name is "{{EscapeAppleScriptString(title)}}"
    if (count of existingNotes) is greater than 0 then
        return "ERROR: A note named '{{EscapeAppleScriptString(title)}}' already exists in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    make new note at folderRef with properties {name:"{{EscapeAppleScriptString(title)}}", body:"{{EscapeAppleScriptString(ToHtmlBody(content))}}"}
    return "OK:Created note '{{EscapeAppleScriptString(title)}}' in '{{EscapeAppleScriptString(folder)}}'."
end tell
""";

    private static string BuildUpdateScript(string folder, string title, string content) =>
        $$"""
tell application "Notes"
{{FindFolderPreamble(folder)}}
    set matchingNotes to every note of folderRef whose name is "{{EscapeAppleScriptString(title)}}"

    if (count of matchingNotes) is 0 then
        return "ERROR: Note '{{EscapeAppleScriptString(title)}}' was not found in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    if (count of matchingNotes) is greater than 1 then
        return "ERROR: Multiple notes named '{{EscapeAppleScriptString(title)}}' exist in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    set noteRef to item 1 of matchingNotes
    set body of noteRef to "{{EscapeAppleScriptString(ToHtmlBody(content))}}"
    return "OK:Updated note '{{EscapeAppleScriptString(title)}}' in '{{EscapeAppleScriptString(folder)}}'."
end tell
""";

    private static string BuildRenameScript(string folder, string title, string newTitle) =>
        $$"""
tell application "Notes"
{{FindFolderPreamble(folder)}}
    set matchingNotes to every note of folderRef whose name is "{{EscapeAppleScriptString(title)}}"
    set conflictingNotes to every note of folderRef whose name is "{{EscapeAppleScriptString(newTitle)}}"

    if (count of matchingNotes) is 0 then
        return "ERROR: Note '{{EscapeAppleScriptString(title)}}' was not found in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    if (count of matchingNotes) is greater than 1 then
        return "ERROR: Multiple notes named '{{EscapeAppleScriptString(title)}}' exist in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    if "{{EscapeAppleScriptString(title)}}" is not "{{EscapeAppleScriptString(newTitle)}}" and (count of conflictingNotes) is greater than 0 then
        return "ERROR: A note named '{{EscapeAppleScriptString(newTitle)}}' already exists in the '{{EscapeAppleScriptString(folder)}}' folder."
    end if

    set noteRef to item 1 of matchingNotes
    set name of noteRef to "{{EscapeAppleScriptString(newTitle)}}"
    return "OK:Renamed note '{{EscapeAppleScriptString(title)}}' to '{{EscapeAppleScriptString(newTitle)}}' in '{{EscapeAppleScriptString(folder)}}'."
end tell
""";

    // --- Helpers ---

    private static List<string> ParseLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string EscapeAppleScriptString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

    /// <summary>
    /// Wrap content for Apple Notes body property. If the content already contains HTML tags,
    /// pass it through as-is (wrapped in a div). Otherwise treat as plain text with line breaks.
    /// </summary>
    private static string ToHtmlBody(string content)
    {
        if (content.Contains('<'))
        {
            // Already HTML — wrap in div if not already wrapped
            return content.TrimStart().StartsWith("<div", StringComparison.OrdinalIgnoreCase)
                ? content
                : $"<div>{content}</div>";
        }

        // Plain text fallback
        var escaped = content
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

        return "<div>" + escaped.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal) + "</div>";
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    private readonly record struct ScriptResult(bool Success, string Message);
}
