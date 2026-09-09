using System.Text;

namespace Achates.Server.Tools;

/// <summary>
/// Extracts message text from the <c>attributedBody</c> column of the macOS Messages
/// database. Messages stores content as an NSArchiver "typedstream" and leaves the
/// plain <c>text</c> column NULL for most messages, so reading <c>text</c> alone
/// misses the majority of a conversation.
///
/// Only the string payload is recovered; the styling attributes that follow it are
/// not needed to read a conversation.
/// </summary>
internal static class AttributedBodyDecoder
{
    private static readonly byte[] StringClassMarker = "NSString"u8.ToArray();

    /// <summary>Introduces the length-prefixed payload after the class chain.</summary>
    private const byte PayloadMarker = 0x2b;

    /// <summary>Lengths at or above this are escaped rather than stored inline.</summary>
    private const byte InlineLengthLimit = 0x81;

    private const byte UInt16LengthMarker = 0x81;
    private const byte Int32LengthMarker = 0x82;

    /// <summary>How far past the class name to look for the payload marker.</summary>
    private const int MarkerSearchWindow = 16;

    /// <summary>
    /// Object replacement character. Messages uses it to reserve the position of an
    /// attachment inside the text run; callers describe attachments themselves, so
    /// it carries nothing worth showing.
    /// </summary>
    private const char AttachmentPlaceholder = '\uFFFC';

    /// <summary>
    /// Returns the message text, or null when the blob carries no readable payload
    /// (absent, truncated, or an archive shape this does not understand). Callers get
    /// null rather than an exception so an unreadable blob degrades to a visible gap
    /// instead of failing the whole read.
    /// </summary>
    public static string? Decode(byte[]? attributedBody)
    {
        if (attributedBody is null || attributedBody.Length == 0)
            return null;

        var blob = attributedBody.AsSpan();

        var classIndex = blob.IndexOf(StringClassMarker);
        if (classIndex < 0)
            return null;

        var cursor = classIndex + StringClassMarker.Length;
        var markerIndex = FindPayloadMarker(blob, cursor);
        if (markerIndex < 0)
            return null;

        cursor = markerIndex + 1;
        if (!TryReadLength(blob, ref cursor, out var length))
            return null;

        if (length <= 0 || cursor + length > blob.Length)
            return null;

        var decoded = Encoding.UTF8.GetString(blob.Slice(cursor, length));
        decoded = decoded.Replace(AttachmentPlaceholder.ToString(), string.Empty).Trim();

        return decoded.Length == 0 ? null : decoded;
    }

    private static int FindPayloadMarker(ReadOnlySpan<byte> blob, int from)
    {
        var limit = Math.Min(from + MarkerSearchWindow, blob.Length);
        for (var i = from; i < limit; i++)
        {
            if (blob[i] == PayloadMarker)
                return i;
        }

        return -1;
    }

    private static bool TryReadLength(ReadOnlySpan<byte> blob, ref int cursor, out int length)
    {
        length = 0;
        if (cursor >= blob.Length)
            return false;

        var marker = blob[cursor++];

        if (marker < InlineLengthLimit)
        {
            length = marker;
            return true;
        }

        switch (marker)
        {
            case UInt16LengthMarker when cursor + 2 <= blob.Length:
                length = blob[cursor] | (blob[cursor + 1] << 8);
                cursor += 2;
                return true;

            case Int32LengthMarker when cursor + 4 <= blob.Length:
                length = blob[cursor]
                    | (blob[cursor + 1] << 8)
                    | (blob[cursor + 2] << 16)
                    | (blob[cursor + 3] << 24);
                cursor += 4;
                return length >= 0;

            default:
                return false;
        }
    }
}
