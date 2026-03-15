using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

/// <summary>
/// Transcribes audio files by sending them to an audio-capable model.
/// </summary>
internal sealed class TranscribeTool(Model model) : AgentTool
{
    private static readonly JsonElement _schema = ObjectSchema(
        new Dictionary<string, JsonElement>
        {
            ["file"] = StringSchema("Absolute path to the audio file to transcribe."),
        },
        required: ["file"]);

    private static readonly Dictionary<string, string> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".caf"] = "caf",
        [".amr"] = "amr",
        [".mp3"] = "mp3",
        [".wav"] = "wav",
        [".m4a"] = "m4a",
        [".aac"] = "aac",
        [".ogg"] = "ogg",
        [".flac"] = "flac",
        [".opus"] = "opus",
    };

    public override string Name => "transcribe";
    public override string Description => "Transcribe an audio file to text.";
    public override string Label => "Transcribe";
    public override JsonElement Parameters => _schema;

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        var file = GetString(arguments, "file");
        if (string.IsNullOrWhiteSpace(file))
            return TextResult("file is required.");

        if (!File.Exists(file))
            return TextResult($"File not found: {file}");

        var ext = Path.GetExtension(file);
        if (!FormatMap.TryGetValue(ext, out var format))
            return TextResult($"Unsupported audio format: {ext}");

        byte[] audioData;
        try
        {
            audioData = await File.ReadAllBytesAsync(file, cancellationToken);
        }
        catch (Exception ex)
        {
            return TextResult($"Failed to read file: {ex.Message}");
        }

        var base64 = Convert.ToBase64String(audioData);

        var context = new CompletionContext
        {
            SystemPrompt = "You are a transcription assistant. Transcribe the audio exactly as spoken. Output only the transcript text, nothing else.",
            Messages =
            [
                new CompletionUserContentMessage
                {
                    Content =
                    [
                        new CompletionAudioInputContent { Data = base64, Format = format },
                        new CompletionTextContent { Text = "Transcribe this audio." },
                    ],
                },
            ],
        };

        try
        {
            var stream = model.Provider.GetCompletions(model, context, null, cancellationToken);

            // Drain the stream to get the result
            await foreach (var _ in stream.WithCancellation(cancellationToken)) { }

            var result = await stream.ResultAsync;

            if (result.ErrorMessage is not null)
                return TextResult($"Transcription failed: {result.ErrorMessage}");

            var transcript = new StringBuilder();
            foreach (var content in result.Content)
            {
                if (content is CompletionTextContent text)
                    transcript.Append(text.Text);
            }

            return transcript.Length > 0
                ? TextResult(transcript.ToString())
                : TextResult("No transcript was generated.");
        }
        catch (Exception ex)
        {
            return TextResult($"Transcription failed: {ex.Message}");
        }
    }

    private static AgentToolResult TextResult(string text) =>
        new() { Content = [new CompletionTextContent { Text = text }] };

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() : val?.ToString();
}
