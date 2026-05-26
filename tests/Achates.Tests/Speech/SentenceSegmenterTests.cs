using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SentenceSegmenterTests
{
    [Fact]
    public void Emits_complete_sentences_on_terminal_punctuation()
    {
        var seg = new SentenceSegmenter();
        var out1 = seg.Push("Hello there. ");
        var out2 = seg.Push("How are you? I am ");
        var out3 = seg.Push("fine!");
        var tail = seg.Flush();

        Assert.Equal(new[] { "Hello there." }, out1);
        Assert.Equal(new[] { "How are you?" }, out2);
        Assert.Empty(out3);
        Assert.Equal(new[] { "I am fine!" }, tail);
    }

    [Fact]
    public void Treats_known_abbreviations_as_non_terminal()
    {
        var seg = new SentenceSegmenter();
        var sentences = seg.Push("Dr. Smith said e.g. cats are nice. Hi.");
        Assert.Equal(new[] { "Dr. Smith said e.g. cats are nice.", "Hi." }, sentences);
    }

    [Fact]
    public void Force_flush_after_max_chars_without_terminal_punctuation()
    {
        var seg = new SentenceSegmenter(maxChars: 50);
        var longRun = new string('a', 60);
        var sentences = seg.Push(longRun);
        Assert.Single(sentences);
        Assert.Equal(50, sentences[0].Length);
    }

    [Fact]
    public void Suppresses_segmentation_inside_code_fence()
    {
        var seg = new SentenceSegmenter();
        var sentences1 = seg.Push("Look at this. ```py\nprint('hi.')\n```\nDone.");
        // The terminal '.' inside the fence must not split.
        Assert.Equal(new[] { "Look at this.", "```py\nprint('hi.')\n```\nDone." }, sentences1);
    }

    [Fact]
    public void Flush_returns_buffered_remainder()
    {
        var seg = new SentenceSegmenter();
        seg.Push("No terminator yet");
        var tail = seg.Flush();
        Assert.Equal(new[] { "No terminator yet" }, tail);
    }

    [Fact]
    public void Flush_returns_empty_when_buffer_drained()
    {
        var seg = new SentenceSegmenter();
        seg.Push("Done. ");
        var tail = seg.Flush();
        Assert.Empty(tail);
    }

    [Fact]
    public void Splits_on_bang_or_question_when_whitespace_arrives_in_later_push()
    {
        var seg = new SentenceSegmenter();
        var out1 = seg.Push("Hello!");
        var out2 = seg.Push(" World.");

        Assert.Empty(out1);                       // ! at end-of-push doesn't emit yet (no whitespace)
        Assert.Equal(new[] { "Hello!", "World." }, out2); // whitespace in next push confirms the split
    }
}
