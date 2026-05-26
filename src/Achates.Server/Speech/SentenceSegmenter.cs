using System.Text;

namespace Achates.Server.Speech;

/// <summary>
/// Buffers a streaming text delta source and emits complete sentences when
/// terminal punctuation (.!?) followed by whitespace or end-of-stream is seen.
/// Tracks code-fence (```) state so terminal punctuation inside a code block
/// does not produce a split; the whole fence ends up in the next emitted chunk.
/// </summary>
public sealed class SentenceSegmenter
{
    private static readonly string[] Abbreviations =
        ["dr.", "mr.", "mrs.", "ms.", "i.e.", "e.g.", "etc.", "vs.", "st."];

    private readonly StringBuilder _buffer = new();
    private readonly int _maxChars;
    private bool _inFence;
    private bool _pendingTerminator;  // last non-space char was a terminal punctuation char
    private int _backtickRun;

    public SentenceSegmenter(int maxChars = 280)
    {
        _maxChars = maxChars;
    }

    /// <summary>Append text; return any sentences that became complete.</summary>
    public IReadOnlyList<string> Push(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var emitted = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            _buffer.Append(ch);
            TrackFence(ch);

            if (!_inFence)
            {
                if (ch is '.' or '!' or '?')
                {
                    // Check if whitespace follows (lookahead within this push)
                    var hasWhitespaceNext = i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]);
                    // Period also triggers at end-of-push (no more chars in this call)
                    var isPeriodAtEnd = ch == '.' && i == text.Length - 1;

                    if ((hasWhitespaceNext || isPeriodAtEnd) && !EndsWithAbbreviation())
                    {
                        Emit(emitted);
                        _pendingTerminator = false;
                    }
                    else
                    {
                        _pendingTerminator = true;
                    }
                }
                else if (char.IsWhiteSpace(ch) && _pendingTerminator)
                {
                    // Whitespace arrived after a '!' or '?' that was end of a prior push
                    // Re-check: buffer currently ends with whitespace, trim it for abbreviation check
                    var trimmed = _buffer.ToString().TrimEnd();
                    if (!EndsWithAbbreviationStr(trimmed))
                    {
                        // Discard trailing whitespace; emit only the trimmed sentence.
                        var s = trimmed;
                        _buffer.Clear();
                        if (s.Length > 0)
                            emitted.Add(s.TrimStart());
                        _pendingTerminator = false;
                    }
                    else
                    {
                        _pendingTerminator = false;
                    }
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    _pendingTerminator = false;
                }
            }

            if (_buffer.Length >= _maxChars)
            {
                Emit(emitted);
                _pendingTerminator = false;
            }
        }
        return emitted;
    }

    /// <summary>Drain any remaining buffered text as a final sentence.</summary>
    public IReadOnlyList<string> Flush()
    {
        _pendingTerminator = false;
        var emitted = new List<string>();
        if (_buffer.Length > 0)
            Emit(emitted);
        return emitted;
    }

    private void Emit(List<string> emitted)
    {
        var s = _buffer.ToString().Trim();
        if (s.Length > 0)
            emitted.Add(s);
        _buffer.Clear();
    }

    private void TrackFence(char ch)
    {
        if (ch == '`')
        {
            _backtickRun++;
            if (_backtickRun == 3)
            {
                _inFence = !_inFence;
                _backtickRun = 0;
            }
        }
        else
        {
            _backtickRun = 0;
        }
    }

    private bool EndsWithAbbreviation()
        => EndsWithAbbreviationStr(_buffer.ToString());

    private static bool EndsWithAbbreviationStr(string s)
    {
        foreach (var abbr in Abbreviations)
        {
            if (s.EndsWith(abbr, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
