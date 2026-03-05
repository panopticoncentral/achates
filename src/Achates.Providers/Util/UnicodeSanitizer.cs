using System.Text;

namespace Achates.Providers.Util;

/// <summary>
/// Removes unpaired Unicode surrogate characters from strings.
/// Valid emoji and supplementary characters (properly paired surrogates) are preserved.
/// </summary>
internal static class UnicodeSanitizer
{
    /// <summary>
    /// Remove unpaired Unicode surrogates that cause JSON serialization errors.
    /// </summary>
    public static string SanitizeSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var hasUnpaired = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    hasUnpaired = true;
                    break;
                }
                i++; // skip the low surrogate
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                hasUnpaired = true;
                break;
            }
        }

        if (!hasUnpaired)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(text[i]);
                    sb.Append(text[i + 1]);
                    i++;
                }
                // else: unpaired high surrogate, skip
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                // Unpaired low surrogate, skip
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }
}
