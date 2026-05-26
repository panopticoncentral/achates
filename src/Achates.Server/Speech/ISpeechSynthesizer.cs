namespace Achates.Server.Speech;

/// <summary>
/// Engine-agnostic TTS synthesizer. The concrete implementation today is
/// <see cref="KokoroSpeechSynthesizer"/>; the interface exists so we can
/// swap in alternatives (ElevenLabs, gpt-4o-audio) per-session in the future
/// without touching the call sites in <see cref="SpeechBroker"/>.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// Synthesize <paramref name="text"/> in the named voice and return the
    /// complete audio bytes plus the format (e.g. "mp3"). May throw on
    /// network errors or non-2xx HTTP responses; callers must handle
    /// failures.
    /// </summary>
    Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct);

    /// <summary>
    /// List of voice ids known to the synthesizer (e.g. for populating an
    /// iOS picker). Returns an empty list if the synthesizer is not
    /// reachable.
    /// </summary>
    Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct);
}

public sealed record SynthesisResult(byte[] Audio, string Format);
