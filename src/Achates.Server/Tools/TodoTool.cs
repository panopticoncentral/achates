using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Safe, constrained access to a Markdown todo list.
/// Supports list, add, complete, and uncomplete operations.
/// Never deletes items — complete marks them done, uncomplete marks them open.
/// </summary>
internal sealed partial class TodoTool(string filePath) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["action"] = StringEnum(
                ["list", "add", "complete", "uncomplete", "move", "reorder", "create_section"],
                "Action to perform.",
                "list"),
            ["section"] = StringSchema(
                "Section name (e.g. 'Today', 'This Week', 'Sometime'). Required for 'add', 'move' (target section), and 'create_section'."),
            ["text"] = StringSchema(
                "Todo item text (without checkbox or emoji prefix). Required for 'add'. For 'complete'/'uncomplete'/'move', a substring to match the item."),
            ["category"] = StringSchema(
                "Emoji category prefix (e.g. '🏠', '💵'). Required for 'add'. Omit for other actions."),
            ["after"] = StringSchema(
                "For 'reorder': substring to match the item to place after. If omitted, moves to the top of the section."),
            ["after_section"] = StringSchema(
                "For 'create_section': insert the new section after this existing section. If omitted, appends at the end."),
        },
        required: ["action"]);

    public override string Name => "todo";
    public override string Description => "Manage the todo list: list, add, complete/uncomplete, move items between sections, reorder items within a section, or create new sections. Cannot delete items.";
    public override string Label => "Todo List";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var action = GetString(arguments, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(),
            "add" => await AddAsync(
                GetString(arguments, "section"),
                GetString(arguments, "text"),
                GetString(arguments, "category")),
            "complete" => await SetCompletionAsync(GetString(arguments, "text"), completed: true),
            "uncomplete" => await SetCompletionAsync(GetString(arguments, "text"), completed: false),
            "move" => await MoveAsync(GetString(arguments, "text"), GetString(arguments, "section")),
            "reorder" => await ReorderAsync(GetString(arguments, "text"), GetString(arguments, "after")),
            "create_section" => await CreateSectionAsync(
                GetString(arguments, "section"),
                GetString(arguments, "after_section")),
            _ => TextResult($"Unknown action: {action}"),
        };
    }

    private async Task<AgentToolResult> ListAsync()
    {
        if (!File.Exists(filePath))
            return TextResult("Todo list file not found.");

        var content = await File.ReadAllTextAsync(filePath);
        return TextResult(content);
    }

    private async Task<AgentToolResult> AddAsync(string? section, string? text, string? category)
    {
        if (string.IsNullOrWhiteSpace(section))
            return TextResult("Section is required when adding a todo.");
        if (string.IsNullOrWhiteSpace(text))
            return TextResult("Text is required when adding a todo.");
        if (string.IsNullOrWhiteSpace(category))
            return TextResult("Category emoji is required when adding a todo.");

        var lines = await ReadLinesAsync();
        var sectionHeader = $"### {section}";
        var insertIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                // Find end of this section (next ### header or end of file)
                insertIndex = i + 1;
                while (insertIndex < lines.Count && !lines[insertIndex].TrimStart().StartsWith("### "))
                {
                    insertIndex++;
                }

                break;
            }
        }

        if (insertIndex < 0)
            return TextResult($"Section '{section}' not found. Available sections: {string.Join(", ", GetSections(lines))}");

        var newItem = $"- [ ] {category}{text}";
        lines.Insert(insertIndex, newItem);
        await WriteLinesAsync(lines);

        return TextResult($"Added to {section}: {newItem}");
    }

    private async Task<AgentToolResult> SetCompletionAsync(string? searchText, bool completed)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return TextResult("Text is required to identify the todo item.");

        var lines = await ReadLinesAsync();
        var match = FindUniqueMatch(lines, searchText);
        if (match.Error is not null)
            return TextResult(match.Error);

        var lineIndex = match.Index;
        var original = lines[lineIndex];

        if (completed)
        {
            if (original.Contains("- [x]"))
                return TextResult($"Already completed: {original.Trim()}");
            lines[lineIndex] = original.Replace("- [ ]", "- [x]");
        }
        else
        {
            if (original.Contains("- [ ]"))
                return TextResult($"Already incomplete: {original.Trim()}");
            lines[lineIndex] = original.Replace("- [x]", "- [ ]");
        }

        await WriteLinesAsync(lines);

        var verb = completed ? "Completed" : "Uncompleted";
        return TextResult($"{verb}: {lines[lineIndex].Trim()}");
    }

    private async Task<AgentToolResult> MoveAsync(string? searchText, string? targetSection)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return TextResult("Text is required to identify the todo item.");
        if (string.IsNullOrWhiteSpace(targetSection))
            return TextResult("Section is required as the move target.");

        var lines = await ReadLinesAsync();
        var match = FindUniqueMatch(lines, searchText);
        if (match.Error is not null)
            return TextResult(match.Error);

        var lineIndex = match.Index;

        // Collect the item and any indented sub-items beneath it
        var itemLines = new List<string> { lines[lineIndex] };
        var nextIndex = lineIndex + 1;
        while (nextIndex < lines.Count && lines[nextIndex].StartsWith('\t'))
        {
            itemLines.Add(lines[nextIndex]);
            nextIndex++;
        }

        // Find target section
        var sectionHeader = $"### {targetSection}";
        var insertIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                insertIndex = i + 1;
                while (insertIndex < lines.Count && !lines[insertIndex].TrimStart().StartsWith("### "))
                    insertIndex++;
                break;
            }
        }

        if (insertIndex < 0)
            return TextResult($"Section '{targetSection}' not found. Available sections: {string.Join(", ", GetSections(lines))}");

        // Remove from original location first
        lines.RemoveRange(lineIndex, itemLines.Count);

        // Adjust insert index if removal was before it
        if (lineIndex < insertIndex)
            insertIndex -= itemLines.Count;

        lines.InsertRange(insertIndex, itemLines);
        await WriteLinesAsync(lines);

        return TextResult($"Moved to {targetSection}: {itemLines[0].Trim()}");
    }

    private async Task<AgentToolResult> ReorderAsync(string? searchText, string? afterText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return TextResult("Text is required to identify the todo item.");

        var lines = await ReadLinesAsync();
        var match = FindUniqueMatch(lines, searchText);
        if (match.Error is not null)
            return TextResult(match.Error);

        var lineIndex = match.Index;

        // Collect the item and any indented sub-items beneath it
        var itemLines = new List<string> { lines[lineIndex] };
        var nextIndex = lineIndex + 1;
        while (nextIndex < lines.Count && lines[nextIndex].StartsWith('\t'))
        {
            itemLines.Add(lines[nextIndex]);
            nextIndex++;
        }

        // Find the section boundaries for this item
        var sectionStart = lineIndex;
        while (sectionStart > 0 && !lines[sectionStart].TrimStart().StartsWith("### "))
            sectionStart--;

        var sectionEnd = lineIndex + 1;
        while (sectionEnd < lines.Count && !lines[sectionEnd].TrimStart().StartsWith("### "))
            sectionEnd++;

        // Remove from original location
        lines.RemoveRange(lineIndex, itemLines.Count);
        sectionEnd -= itemLines.Count;

        int insertIndex;
        if (string.IsNullOrWhiteSpace(afterText))
        {
            // Move to top of section (first line after the header)
            insertIndex = sectionStart + 1;
        }
        else
        {
            // Find the "after" item within the same section
            var afterMatch = FindUniqueMatch(lines[sectionStart..sectionEnd], afterText);
            if (afterMatch.Error is not null)
                return TextResult(afterMatch.Error);

            var afterIndex = sectionStart + afterMatch.Index;

            // Skip past the after item's sub-items
            insertIndex = afterIndex + 1;
            while (insertIndex < sectionEnd && lines[insertIndex].StartsWith('\t'))
                insertIndex++;
        }

        lines.InsertRange(insertIndex, itemLines);
        await WriteLinesAsync(lines);

        var position = string.IsNullOrWhiteSpace(afterText) ? "top of section" : $"after '{afterText}'";
        return TextResult($"Reordered to {position}: {itemLines[0].Trim()}");
    }

    private async Task<AgentToolResult> CreateSectionAsync(string? sectionName, string? afterSection)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return TextResult("Section name is required.");

        var lines = await ReadLinesAsync();
        var sectionHeader = $"### {sectionName}";

        // Check if section already exists
        if (lines.Any(l => l.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase)))
            return TextResult($"Section '{sectionName}' already exists.");

        if (afterSection is not null)
        {
            var afterHeader = $"### {afterSection}";
            var afterIndex = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Equals(afterHeader, StringComparison.OrdinalIgnoreCase))
                {
                    // Find end of the "after" section
                    afterIndex = i + 1;
                    while (afterIndex < lines.Count && !lines[afterIndex].TrimStart().StartsWith("### "))
                        afterIndex++;
                    break;
                }
            }

            if (afterIndex < 0)
                return TextResult($"Section '{afterSection}' not found. Available sections: {string.Join(", ", GetSections(lines))}");

            lines.Insert(afterIndex, sectionHeader);
        }
        else
        {
            // Append at end
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
        }

        await WriteLinesAsync(lines);
        return TextResult($"Created section: {sectionName}");
    }

    private async Task<List<string>> ReadLinesAsync()
    {
        if (!File.Exists(filePath))
            return [];

        var content = await File.ReadAllTextAsync(filePath);
        return [.. content.Split('\n')];
    }

    private async Task WriteLinesAsync(List<string> lines)
    {
        await File.WriteAllTextAsync(filePath, string.Join('\n', lines));
    }

    private static (int Index, string? Error) FindUniqueMatch(List<string> lines, string searchText)
    {
        var matches = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (CheckboxPattern().IsMatch(lines[i]) &&
                lines[i].Contains(searchText, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        if (matches.Count == 0)
            return (-1, $"No todo item found matching '{searchText}'.");

        if (matches.Count > 1)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Multiple items match '{searchText}'. Be more specific:");
            foreach (var idx in matches)
                sb.AppendLine($"  {lines[idx].Trim()}");
            return (-1, sb.ToString().TrimEnd());
        }

        return (matches[0], null);
    }

    private static List<string> GetSections(List<string> lines) =>
        lines.Where(l => l.TrimStart().StartsWith("### "))
             .Select(l => l.Trim()[4..])
             .ToList();

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();

    [GeneratedRegex(@"^[\s]*- \[([ x])\] ")]
    private static partial Regex CheckboxPattern();
}
