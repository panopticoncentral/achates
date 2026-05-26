namespace Achates.Server.Speech;

/// <summary>
/// The sink that <see cref="SpeechBroker"/> calls back into to deliver
/// audio events to wherever they need to go (the WebSocket transport in
/// production; a recorder in tests).
/// </summary>
public interface ISpeechSink
{
    Task EmitAudioBlockAsync(string turnId, int sentenceIndex, string voice, string format, byte[] data, string text, CancellationToken ct);
    Task EmitAudioErrorAsync(string turnId, int? sentenceIndex, string message, CancellationToken ct);
}

/// <summary>
/// Per-turn orchestrator: consumes streaming text deltas, segments and
/// sanitizes them, synthesizes each completed sentence sequentially, and
/// forwards the resulting audio events through an <see cref="ISpeechSink"/>.
/// Sequential per-sentence processing is intentional so audio plays in order
/// downstream.
/// </summary>
public sealed class SpeechBroker(
    ISpeechSynthesizer synth,
    ISpeechSink sink,
    string voice,
    string turnId)
{
    private readonly SentenceSegmenter _segmenter = new();
    private int _sentenceIndex;
    private bool _synthFailedForTurn;

    public async Task PushTextAsync(string text, CancellationToken ct = default)
    {
        var sentences = _segmenter.Push(text);
        foreach (var sentence in sentences)
            await SynthesizeAndEmitAsync(sentence, ct);
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        var tail = _segmenter.Flush();
        foreach (var sentence in tail)
            await SynthesizeAndEmitAsync(sentence, ct);
    }

    private async Task SynthesizeAndEmitAsync(string raw, CancellationToken ct)
    {
        var spoken = SpeechSanitizer.Sanitize(raw).Trim();
        if (string.IsNullOrWhiteSpace(spoken))
            return; // Nothing speakable; skip silently.

        // Once the synthesizer has failed in this turn, skip the rest — there's
        // no point spamming HTTP requests at a dead endpoint and we already
        // surfaced one audio.error to the client.
        if (_synthFailedForTurn)
            return;

        var index = _sentenceIndex++;
        try
        {
            var result = await synth.SynthesizeAsync(spoken, voice, ct);
            await sink.EmitAudioBlockAsync(turnId, index, voice, result.Format, result.Audio, spoken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _synthFailedForTurn = true;
            await sink.EmitAudioErrorAsync(turnId, index, ex.Message, ct);
        }
    }
}
