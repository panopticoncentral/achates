namespace Achates.Server.Speech;

/// <summary>
/// Constants and helpers for the TTS rate ("speed") parameter. Centralized
/// because the same clamp range is enforced on three paths: AGENT.md parsing,
/// the <c>agent.update</c> RPC, and the <c>speech.test</c> RPC.
/// </summary>
public static class SpeechRate
{
    /// <summary>Kokoro-FastAPI's documented minimum, enforced by Pydantic on the server.</summary>
    public const double Min = 0.25;

    /// <summary>Kokoro-FastAPI's documented maximum, enforced by Pydantic on the server.</summary>
    public const double Max = 4.0;

    /// <summary>Kokoro's own default — when this is the requested rate we omit the field entirely.</summary>
    public const double Default = 1.0;

    /// <summary>Clamp <paramref name="value"/> to <see cref="Min"/>..<see cref="Max"/>.</summary>
    public static double Clamp(double value) => Math.Clamp(value, Min, Max);
}
