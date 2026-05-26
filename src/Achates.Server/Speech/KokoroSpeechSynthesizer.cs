using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server.Speech;

/// <summary>
/// HTTP client targeting the OpenAI-compatible /v1/audio/speech endpoint
/// exposed by kokoro-fastapi (or any other Kokoro server with the same
/// shape). Availability is gated externally by <see cref="KokoroSidecarProcess"/>
/// which calls <see cref="MarkAvailable"/> after a successful health probe.
/// </summary>
public sealed class KokoroSpeechSynthesizer(HttpClient http, Uri baseUrl) : ISpeechSynthesizer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private volatile bool _available;

    public bool IsAvailable => _available;

    /// <summary>Set by the sidecar supervisor after a successful health probe.</summary>
    public void MarkAvailable(bool value) => _available = value;

    public async Task<SynthesisResult> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        var url = new Uri(baseUrl, "/v1/audio/speech");
        var body = new SpeechRequest("kokoro", voice, text, "mp3");

        var response = await http.PostAsJsonAsync(url, body, Json, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new SynthesisResult(bytes, "mp3");
    }

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
    {
        try
        {
            var url = new Uri(baseUrl, "/v1/audio/voices");
            await using var stream = await http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ExtractVoiceIds(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses the three response shapes kokoro-fastapi has shipped over time:
    /// (1) a bare JSON string array, (2) <c>{"voices": ["af_x", ...]}</c>, and
    /// (3) <c>{"voices": [{"id": "af_x", "name": "..."}, ...]}</c> — the current
    /// (1.x) shape. Objects fall back to <c>name</c> when <c>id</c> is missing.
    /// Unknown shapes silently degrade to an empty list.
    /// </summary>
    internal static IReadOnlyList<string> ExtractVoiceIds(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("voices", out var v) ? v : default);

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    if (item.GetString() is { Length: > 0 } s)
                        list.Add(s);
                    break;
                case JsonValueKind.Object:
                    if (TryGetString(item, "id", out var id))
                        list.Add(id);
                    else if (TryGetString(item, "name", out var name))
                        list.Add(name);
                    break;
            }
        }
        return list;
    }

    private static bool TryGetString(JsonElement obj, string property, out string value)
    {
        if (obj.TryGetProperty(property, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && prop.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    private sealed record SpeechRequest(string Model, string Voice, string Input, string ResponseFormat);
}
