using System.Text;

namespace Achates.Tests;

/// <summary>
/// Builds <c>attributedBody</c> blobs in the shape the real macOS Messages database
/// uses. The header is the class-name scaffolding every such blob carries, captured
/// from a real database; only the payload is synthesized.
/// </summary>
internal static class TypedstreamFixture
{
    private const string HeaderHex =
        "040b73747265616d747970656481e803840140848484194e534d757461626c65417474726962757465" +
        "64537472696e67008484124e5341747472696275746564537472696e67008484084e534f626a656374" +
        "0085928484840f4e534d757461626c65537472696e67018484084e53537472696e67019584012b";

    /// <summary>Trailing archive bytes that follow the payload in a real blob.</summary>
    private static readonly byte[] Trailer = [0x86, 0x84, 0x02, 0x69, 0x49, 0x01];

    /// <summary>
    /// Lengths under 0x81 are stored inline; larger ones are escaped with 0x81
    /// (ushort) or 0x82 (int), little-endian.
    /// </summary>
    public static byte[] Build(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var bytes = new List<byte>(Convert.FromHexString(HeaderHex));

        if (payload.Length < 0x81)
        {
            bytes.Add((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            bytes.Add(0x81);
            bytes.AddRange(BitConverter.GetBytes((ushort)payload.Length));
        }
        else
        {
            bytes.Add(0x82);
            bytes.AddRange(BitConverter.GetBytes(payload.Length));
        }

        bytes.AddRange(payload);
        bytes.AddRange(Trailer);
        return [.. bytes];
    }
}
