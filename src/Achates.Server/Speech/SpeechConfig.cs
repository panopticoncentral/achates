namespace Achates.Server.Speech;

public sealed class SpeechConfig
{
    /// <summary>
    /// Kokoro-FastAPI server URL (e.g. <c>http://127.0.0.1:8880</c>). Achates
    /// does not manage the sidecar process — you start it yourself (launchd,
    /// systemd, Docker, terminal — your call) and point Achates at it here.
    /// Defaults to <c>http://127.0.0.1:8880</c> when omitted.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Global default voice id used when an agent doesn't declare
    /// <c>**Voice:**</c>. Off by default — voiceless agents stay silent.
    /// </summary>
    public string? DefaultVoice { get; set; }
}
