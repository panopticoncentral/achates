using System.Text;
using Achates.Server.Tools;

namespace Achates.Tests;

/// <summary>
/// macOS Messages stores message content in the <c>attributedBody</c> column — an
/// NSArchiver "typedstream" — and leaves <c>text</c> NULL for a large share of
/// messages. Reading only <c>text</c> makes those messages invisible.
///
/// Fixtures are built by <see cref="TypedstreamFixture"/>, which reproduces the
/// byte layout of a real blob around a synthesized payload.
/// </summary>
public class AttributedBodyDecoderTests
{
    private static byte[] Blob(string text) => TypedstreamFixture.Build(text);

    [Fact]
    public void Decodes_a_short_message()
    {
        Assert.Equal("huh?", AttributedBodyDecoder.Decode(Blob("huh?")));
    }

    [Fact]
    public void Decodes_a_message_too_long_for_a_single_length_byte()
    {
        var long_message = new string('a', 4121);

        Assert.Equal(long_message, AttributedBodyDecoder.Decode(Blob(long_message)));
    }

    [Fact]
    public void Decodes_a_message_too_long_for_a_two_byte_length()
    {
        var very_long_message = new string('b', 70_000);

        Assert.Equal(very_long_message, AttributedBodyDecoder.Decode(Blob(very_long_message)));
    }

    [Fact]
    public void Reads_the_length_as_bytes_not_characters()
    {
        // Messages substitutes "--" into an em dash, so real payloads are routinely
        // multi-byte. Treating the length as a character count truncates them.
        const string emDashed = "Sure—the deadline moved to Tuesday";

        Assert.Equal(emDashed, AttributedBodyDecoder.Decode(Blob(emDashed)));
    }

    [Fact]
    public void Decodes_a_payload_that_is_entirely_multibyte()
    {
        const string emoji = "👍🏽 ありがとう";

        Assert.Equal(emoji, AttributedBodyDecoder.Decode(Blob(emoji)));
    }

    [Fact]
    public void Returns_null_when_the_payload_is_only_an_attachment_placeholder()
    {
        // Voice notes and photos store U+FFFC as their payload. Surfacing it would
        // put a stray box where the caller already prints its own description.
        Assert.Null(AttributedBodyDecoder.Decode(Blob("\uFFFC")));
    }

    [Fact]
    public void Keeps_the_caption_of_a_message_that_also_carries_an_attachment()
    {
        Assert.Equal("look at this", AttributedBodyDecoder.Decode(Blob("\uFFFClook at this")));
    }

    [Fact]
    public void Returns_null_for_a_blob_with_no_string_payload()
    {
        Assert.Null(AttributedBodyDecoder.Decode(Encoding.ASCII.GetBytes("streamtyped garbage")));
    }

    [Fact]
    public void Returns_null_for_an_empty_payload()
    {
        Assert.Null(AttributedBodyDecoder.Decode(Blob("")));
    }

    [Fact]
    public void Returns_null_rather_than_throwing_on_a_truncated_blob()
    {
        var truncated = Blob("a message that gets cut off mid-payload")[..^20];

        Assert.Null(AttributedBodyDecoder.Decode(truncated));
    }

    [Fact]
    public void Returns_null_for_null_or_empty_input()
    {
        Assert.Null(AttributedBodyDecoder.Decode(null));
        Assert.Null(AttributedBodyDecoder.Decode([]));
    }
}
