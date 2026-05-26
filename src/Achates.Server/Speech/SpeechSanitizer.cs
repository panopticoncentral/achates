using System.Text.RegularExpressions;

namespace Achates.Server.Speech;

/// <summary>
/// Strips markdown noise and unspeakable content from a chunk of assistant
/// text before sending it to the TTS engine. Pragmatic regex-based passes;
/// not a full Markdown parser — keep it simple until it proves insufficient.
/// </summary>
public static partial class SpeechSanitizer
{
    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var s = input;

        // Drop fenced code blocks entirely (multiline, non-greedy).
        s = CodeFenceRegex().Replace(s, "");

        // Image refs ![alt](url) → "" (must run before plain links).
        s = ImageRegex().Replace(s, "");

        // Links [text](url) → "text"
        s = LinkRegex().Replace(s, "$1");

        // Bare URLs → ""
        s = BareUrlRegex().Replace(s, "");

        // Inline code `…` → ""
        s = InlineCodeRegex().Replace(s, "");

        // Horizontal rules → ""
        s = HorizontalRuleRegex().Replace(s, "");

        // Blockquote markers at line start → ""
        s = BlockquoteRegex().Replace(s, "");

        // Headers at line start: drop "# ", keep content.
        s = HeaderRegex().Replace(s, "");

        // Bold/italic markers (** or __ or * or _) — drop the marks only.
        s = EmphasisRegex().Replace(s, "$1");

        // Emoji — match a run of one or more emoji glyphs (with optional
        // horizontal spaces between them) as a single unit, then insert a
        // single space only when there are non-whitespace chars on both sides.
        s = EmojiRegex().Replace(s, m =>
        {
            var pos = m.Index;
            var end = m.Index + m.Length;
            var hasLeft  = pos > 0 && !char.IsWhiteSpace(s[pos - 1]);
            var hasRight = end < s.Length && !char.IsWhiteSpace(s[end]);
            return hasLeft && hasRight ? " " : "";
        });

        // Collapse runs of >2 blank lines.
        s = ExtraBlankLinesRegex().Replace(s, "\n\n");

        return s;
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Multiline)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^\s*-{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^>\s?", RegexOptions.Multiline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?:\*\*|__|\*|_)(.+?)(?:\*\*|__|\*|_)")]
    private static partial Regex EmphasisRegex();

    // Matches a run of one or more emoji glyphs with optional horizontal
    // spaces between them, plus optional leading/trailing horizontal space.
    // A "run" means all contiguous emoji (and the spaces between them) are
    // consumed as a single match so the evaluator makes one space-vs-empty
    // decision for the whole group — preventing double spaces from adjacent emoji.
    // Covers: Miscellaneous Symbols, Dingbats, Emoticons,
    //         Misc Symbols & Pictographs, Transport & Map, Supplemental Symbols.
    [GeneratedRegex(@"[ \t]*(?:[☀-➿]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF])(?:[ \t]*(?:[☀-➿]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]))*[ \t]*")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();
}
