using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Achates.Console;

/// <summary>
/// Streams Markdown text to the console, rendering completed lines with Spectre.Console styling.
/// Handles headers, bold, italic, inline code, fenced code blocks, and lists.
/// </summary>
internal sealed partial class MarkdownRenderer
{
    private readonly StringBuilder _lineBuffer = new();
    private bool _inCodeBlock;
    private string _codeBlockLang = "";
    private readonly StringBuilder _codeBlockContent = new();

    /// <summary>
    /// Feed a text delta. Completed lines are rendered immediately.
    /// </summary>
    public void Write(string delta)
    {
        _lineBuffer.Append(delta);

        // Process all complete lines
        while (true)
        {
            var buf = _lineBuffer.ToString();
            var nl = buf.IndexOf('\n');
            if (nl < 0)
            {
                break;
            }

            var line = buf[..nl];
            _lineBuffer.Clear();
            _lineBuffer.Append(buf[(nl + 1)..]);

            ProcessLine(line);
        }
    }

    /// <summary>
    /// Flush any remaining content (no trailing newline).
    /// </summary>
    public void Flush()
    {
        if (_lineBuffer.Length > 0)
        {
            ProcessLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }

        if (_inCodeBlock)
        {
            RenderCodeBlock();
            _inCodeBlock = false;
        }
    }

    public void Reset()
    {
        _lineBuffer.Clear();
        _inCodeBlock = false;
        _codeBlockLang = "";
        _codeBlockContent.Clear();
    }

    private void ProcessLine(string line)
    {
        // Fenced code block toggle
        if (line.TrimStart().StartsWith("```"))
        {
            if (_inCodeBlock)
            {
                RenderCodeBlock();
                _inCodeBlock = false;
            }
            else
            {
                _inCodeBlock = true;
                _codeBlockLang = line.TrimStart()[3..].Trim();
                _codeBlockContent.Clear();
            }
            return;
        }

        if (_inCodeBlock)
        {
            _codeBlockContent.AppendLine(line);
            return;
        }

        RenderLine(line);
    }

    private void RenderCodeBlock()
    {
        var code = _codeBlockContent.ToString().TrimEnd();
        var header = string.IsNullOrEmpty(_codeBlockLang) ? "Code" : _codeBlockLang;
        var panel = new Panel(code.EscapeMarkup())
        {
            Header = new PanelHeader($"[dim]{header.EscapeMarkup()}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0),
        };
        AnsiConsole.Write(panel);
    }

    private static void RenderLine(string line)
    {
        // Headers
        if (line.StartsWith("### "))
        {
            AnsiConsole.MarkupLine($"[bold]{FormatInline(line[4..].EscapeMarkup())}[/]");
            return;
        }
        if (line.StartsWith("## "))
        {
            AnsiConsole.MarkupLine($"[bold underline]{FormatInline(line[3..].EscapeMarkup())}[/]");
            return;
        }
        if (line.StartsWith("# "))
        {
            AnsiConsole.MarkupLine($"[bold underline]{FormatInline(line[2..].EscapeMarkup())}[/]");
            return;
        }

        // Horizontal rule
        if (line is "---" or "***" or "___")
        {
            AnsiConsole.Write(new Rule().RuleStyle("dim"));
            return;
        }

        // Unordered list
        if (line.Length > 2 && line.TrimStart() is ['*' or '-', ' ', ..])
        {
            var indent = line.Length - line.TrimStart().Length;
            var content = line.TrimStart()[2..];
            var prefix = new string(' ', indent);
            AnsiConsole.MarkupLine($"{prefix} [cyan]•[/] {FormatInline(content.EscapeMarkup())}");
            return;
        }

        // Ordered list
        var orderedMatch = OrderedListRegex().Match(line);
        if (orderedMatch.Success)
        {
            var indent = line.Length - line.TrimStart().Length;
            var num = orderedMatch.Groups[1].Value;
            var content = orderedMatch.Groups[2].Value;
            var prefix = new string(' ', indent);
            AnsiConsole.MarkupLine($"{prefix} [cyan]{num.EscapeMarkup()}.[/] {FormatInline(content.EscapeMarkup())}");
            return;
        }

        // Block quote
        if (line.StartsWith("> "))
        {
            AnsiConsole.MarkupLine($"[dim]│[/] [italic]{FormatInline(line[2..].EscapeMarkup())}[/]");
            return;
        }

        // Regular line
        AnsiConsole.MarkupLine(FormatInline(line.EscapeMarkup()));
    }

    /// <summary>
    /// Apply inline formatting (bold, italic, code) to already-escaped markup text.
    /// </summary>
    private static string FormatInline(string escaped)
    {
        // Inline code (must go first so bold/italic inside code aren't processed)
        escaped = InlineCodeRegex().Replace(escaped, "[grey85 on grey15]$1[/]");

        // Bold
        escaped = BoldRegex().Replace(escaped, "[bold]$1[/]");

        // Italic (careful not to match inside bold markers)
        escaped = ItalicRegex().Replace(escaped, "[italic]$1[/]");

        return escaped;
    }

    // Regex patterns — escaped markup means ** and * are literal (not Spectre tags)
    // Since EscapeMarkup only escapes [ and ], our patterns match raw markdown chars.

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"^\s*(\d+)\.\s+(.+)$")]
    private static partial Regex OrderedListRegex();
}
