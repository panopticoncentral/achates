using System.Collections.Concurrent;
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class SpeechBrokerTests
{
    [Fact]
    public async Task Emits_audio_block_per_complete_sentence_in_order()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello. ");
        await broker.PushTextAsync("How are you? ");
        await broker.PushTextAsync("Goodbye.");
        await broker.FinishAsync();

        Assert.Equal(3, sink.Blocks.Count);
        Assert.Equal("Hello.", sink.Blocks[0].Text);
        Assert.Equal(0, sink.Blocks[0].SentenceIndex);
        Assert.Equal("How are you?", sink.Blocks[1].Text);
        Assert.Equal(1, sink.Blocks[1].SentenceIndex);
        Assert.Equal("Goodbye.", sink.Blocks[2].Text);
        Assert.Equal(2, sink.Blocks[2].SentenceIndex);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task Strips_markdown_before_synthesis()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("**bold** word. ");
        await broker.FinishAsync();

        Assert.Single(sink.Blocks);
        Assert.Equal("bold word.", sink.Blocks[0].Text);
    }

    [Fact]
    public async Task Skips_code_fence_blocks_in_emitted_audio()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello.\n```py\nprint('hi')\n```\nDone.");
        await broker.FinishAsync();

        // Two sentences total: "Hello." then the fence-suppressed rest sanitized to "\n\nDone."
        Assert.True(sink.Blocks.Count >= 1);
        // No emitted audio should contain code-fence content.
        Assert.All(sink.Blocks, b => Assert.DoesNotContain("print(", b.Text));
    }

    [Fact]
    public async Task Synth_failure_emits_audio_error_and_does_not_throw()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth { ThrowOnSynth = true };
        var broker = new SpeechBroker(synth, sink, voice: "bad", turnId: "turn-1");

        await broker.PushTextAsync("Hello.");
        await broker.FinishAsync();

        Assert.Empty(sink.Blocks);
        Assert.Single(sink.Errors);
        Assert.Contains("turn-1", sink.Errors[0].TurnId);
    }

    [Fact]
    public async Task Skips_synthesis_when_synthesizer_unavailable()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth { Available = false };
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("Hello.");
        await broker.FinishAsync();

        Assert.Empty(sink.Blocks);
        Assert.Single(sink.Errors);
        Assert.Contains("unavailable", sink.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlushAsync_drains_trailing_unterminated_sentence()
    {
        var sink = new RecordingSink();
        var synth = new FakeSynth();
        var broker = new SpeechBroker(synth, sink, voice: "af_nicole", turnId: "turn-1");

        await broker.PushTextAsync("No terminator here");
        await broker.FinishAsync();

        Assert.Single(sink.Blocks);
        Assert.Equal("No terminator here", sink.Blocks[0].Text);
    }

    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public bool Available { get; set; } = true;
        public bool ThrowOnSynth { get; set; }
        public bool IsAvailable => Available;
        public Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct)
        {
            if (ThrowOnSynth) throw new HttpRequestException("boom");
            return Task.FromResult(new SynthesisResult(new byte[] { 1, 2, 3 }, "mp3"));
        }
        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed record AudioBlock(string TurnId, int SentenceIndex, string Voice, string Format, byte[] Data, string Text);
    private sealed record AudioError(string TurnId, int? SentenceIndex, string Message);

    private sealed class RecordingSink : ISpeechSink
    {
        private readonly ConcurrentBag<AudioBlock> _blocks = new();
        private readonly ConcurrentBag<AudioError> _errors = new();
        public List<AudioBlock> Blocks => _blocks.OrderBy(b => b.SentenceIndex).ToList();
        public List<AudioError> Errors => _errors.ToList();

        public Task EmitAudioBlockAsync(string turnId, int sentenceIndex, string voice, string format, byte[] data, string text, CancellationToken ct)
        {
            _blocks.Add(new AudioBlock(turnId, sentenceIndex, voice, format, data, text));
            return Task.CompletedTask;
        }

        public Task EmitAudioErrorAsync(string turnId, int? sentenceIndex, string message, CancellationToken ct)
        {
            _errors.Add(new AudioError(turnId, sentenceIndex, message));
            return Task.CompletedTask;
        }
    }
}
