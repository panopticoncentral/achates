using System.Text;

namespace Achates.Providers.OpenRouter;

/// <summary>
/// Strips unpaired UTF-16 surrogates that cause JSON serialization failures.
/// </summary>
public static class UnicodeSanitizer
{
    /// <summary>
    /// Returns the input with any unpaired UTF-16 surrogates replaced by U+FFFD.
    /// Fast-path: if no surrogates are found, returns the original string with zero allocations.
    /// </summary>
    public static string SanitizeSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var needsSanitization = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
            {
                continue;
            }

            if (char.IsHighSurrogate(text[i])
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                i++; // valid pair, skip low surrogate
                continue;
            }

            needsSanitization = true;
            break;
        }

        if (!needsSanitization)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
