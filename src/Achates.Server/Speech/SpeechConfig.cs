namespace Achates.Server.Speech;

public sealed class SpeechConfig
{
    /// <summary>
    /// Managed sidecar process to launch on server startup. Mutually
    /// exclusive with <see cref="Endpoint"/> — if both are set, Endpoint wins
    /// (and a warning is logged).
    /// </summary>
    public SidecarConfig? Sidecar { get; set; }

    /// <summary>
    /// External sidecar URL (e.g. <c>http://127.0.0.1:8880</c>). When set,
    /// Achates does not launch a child process; it only health-checks the
    /// endpoint. Falls back to a value derived from <see cref="Sidecar"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Global default voice id used when an agent doesn't declare
    /// <c>**Voice:**</c>. Off by default — voiceless agents stay silent.
    /// </summary>
    public string? DefaultVoice { get; set; }
}

public sealed class SidecarConfig
{
    public string? WorkingDir { get; set; }
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
}
