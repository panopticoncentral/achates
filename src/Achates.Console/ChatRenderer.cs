using System.Diagnostics;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Console;

internal static class ChatRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";
    private const string Cyan = "\x1b[36m";
    private const string Yellow = "\x1b[33m";
    private const string Green = "\x1b[32m";
    private const string Red = "\x1b[31m";

    public static void WriteHeader(Model model, IReadOnlyList<CompletionTool>? tools)
    {
        System.Console.WriteLine($"{Bold}Achates Chat{Reset}");
        System.Console.WriteLine($"Model: {model.Id} ({model.Name})");

        if (tools is { Count: > 0 })
        {
            var names = string.Join(", ", tools.Select(t => t.Name));
            System.Console.WriteLine($"Tools: {names}");
        }

        System.Console.WriteLine($"Type {Dim}/image <path> [text]{Reset} to send an image, {Dim}/file <path> [text]{Reset} to send a file, {Dim}/audio <path> [text]{Reset} to send audio,");
        System.Console.WriteLine($"     {Dim}/record [text]{Reset} to record from microphone, {Dim}/exit{Reset} to quit.");
        System.Console.WriteLine();
    }

    public static void WritePrompt()
    {
        System.Console.Write($"{Bold}{Cyan}> {Reset}");
    }

    public static async Task<CompletionAssistantMessage> RenderStreamAsync(
        CompletionEventStream stream,
        CancellationToken cancellationToken)
    {
        var inThinking = false;
        StreamingAudioPlayer? audioPlayer = null;

        await foreach (var evt in stream.WithCancellation(cancellationToken))
        {
            switch (evt)
            {
                case CompletionThinkingStartEvent:
                    inThinking = true;
                    System.Console.Write($"{Dim}[thinking] ");
                    break;

                case CompletionThinkingDeltaEvent e:
                    System.Console.Write(e.Delta);
                    break;

                case CompletionThinkingEndEvent:
                    inThinking = false;
                    System.Console.Write(Reset);
                    System.Console.WriteLine();
                    System.Console.WriteLine();
                    break;

                case CompletionTextDeltaEvent e:
                    System.Console.Write(e.Delta);
                    break;

                case CompletionTextEndEvent:
                    System.Console.WriteLine();
                    break;

                case CompletionToolCallEndEvent e:
                    System.Console.WriteLine($"{Yellow}  [{e.CompletionToolCall.Name}]{Reset}");
                    break;

                case CompletionImageEvent e:
                    WriteInlineImage(e.Image);
                    break;

                case CompletionAudioStartEvent:
                    audioPlayer = StreamingAudioPlayer.TryStart();
                    break;

                case CompletionAudioDeltaEvent e:
                    if (e.DataDelta is not null)
                        audioPlayer?.WriteChunk(e.DataDelta);
                    if (e.TranscriptDelta is not null)
                        System.Console.Write(e.TranscriptDelta);
                    break;

                case CompletionAudioEndEvent e:
                    System.Console.WriteLine();
                    audioPlayer?.Finish();
                    audioPlayer = null;
                    SaveAudioFile(e.Content);
                    break;

                case CompletionErrorEvent e:
                    if (inThinking) System.Console.Write(Reset);
                    audioPlayer?.Finish();
                    audioPlayer = null;
                    System.Console.Error.WriteLine($"{Red}Error: {e.Error.ErrorMessage}{Reset}");
                    break;
            }
        }

        audioPlayer?.Finish();
        return await stream.ResultAsync;
    }

    public static void WriteToolExecution(CompletionToolCall toolCall)
    {
        var argsStr = string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
        System.Console.WriteLine($"{Yellow}  {toolCall.Name}({argsStr}){Reset}");
    }

    public static void WriteToolResult(CompletionToolResultMessage result)
    {
        var text = string.Join("\n", result.Content.OfType<CompletionTextContent>().Select(c => c.Text));
        var color = result.IsError ? Red : Green;
        System.Console.WriteLine($"{color}  → {text}{Reset}");
        System.Console.WriteLine();
    }

    private enum ImageProtocol { None, Iterm2, Kitty }

    private static void WriteInlineImage(CompletionImageContent image)
    {
        var ext = image.MimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".png",
        };

        var path = Path.Combine(Path.GetTempPath(), $"achates-{Guid.NewGuid():N}{ext}");
        var bytes = Convert.FromBase64String(image.Data);
        File.WriteAllBytes(path, bytes);

        var protocol = DetectImageProtocol();
        switch (protocol)
        {
            case ImageProtocol.Kitty:
                WriteKittyImage(image.Data);
                break;
            case ImageProtocol.Iterm2:
                var args = $"inline=1;size={bytes.Length};preserveAspectRatio=1";
                System.Console.Write($"\x1b]1337;File={args}:{image.Data}\x07");
                System.Console.WriteLine();
                break;
        }

        System.Console.WriteLine($"{Dim}  [image: {path}]{Reset}");
    }

    private static void WriteKittyImage(string base64Data)
    {
        // Kitty graphics protocol: send base64 data in chunks of up to 4096 bytes.
        // First chunk: a=T (transmit and display), f=100 (PNG), m=1 (more chunks follow)
        // Last chunk: m=0 (final)
        const int chunkSize = 4096;
        var span = base64Data.AsSpan();

        for (var offset = 0; offset < span.Length; offset += chunkSize)
        {
            var remaining = span.Length - offset;
            var length = Math.Min(chunkSize, remaining);
            var chunk = span.Slice(offset, length);
            var isFirst = offset == 0;
            var isLast = offset + length >= span.Length;

            if (isFirst)
                System.Console.Write($"\x1b_Ga=T,f=100,m={(isLast ? 0 : 1)};{chunk}\x1b\\");
            else
                System.Console.Write($"\x1b_Gm={(isLast ? 0 : 1)};{chunk}\x1b\\");
        }

        System.Console.WriteLine();
    }

    private static ImageProtocol DetectImageProtocol()
    {
        var term = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        return term switch
        {
            "ghostty" or "kitty" => ImageProtocol.Kitty,
            "iTerm.app" or "WezTerm" or "vscode" => ImageProtocol.Iterm2,
            _ => ImageProtocol.None,
        };
    }

    private static void SaveAudioFile(CompletionAudioContent audio)
    {
        var bytes = Convert.FromBase64String(audio.Data);

        if (audio.Format == "pcm16")
            bytes = WrapPcm16AsWav(bytes, sampleRate: 24000, channels: 1);

        var ext = audio.Format switch
        {
            "mp3" => ".mp3",
            "opus" => ".ogg",
            "flac" => ".flac",
            _ => ".wav",
        };

        var path = Path.Combine(Path.GetTempPath(), $"achates-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        System.Console.WriteLine($"{Dim}  [audio: {path}]{Reset}");
    }

    private static byte[] WrapPcm16AsWav(byte[] pcmData, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcmData.Length;

        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataSize);
        bw.Write(pcmData);

        return ms.ToArray();
    }

    /// <summary>
    /// Streams raw PCM16 audio to a player process via stdin as chunks arrive.
    /// Falls back gracefully if no suitable player is found.
    /// </summary>
    private sealed class StreamingAudioPlayer
    {
        private readonly Process _process;
        private readonly Stream _stdin;

        private StreamingAudioPlayer(Process process)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
        }

        public static StreamingAudioPlayer? TryStart()
        {
            var (fileName, args) = FindStreamingPlayer();
            if (fileName is null)
                return null;

            try
            {
                var psi = new ProcessStartInfo(fileName, args ?? "")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                var process = Process.Start(psi);
                return process is not null ? new StreamingAudioPlayer(process) : null;
            }
            catch
            {
                return null;
            }
        }

        public void WriteChunk(string base64Delta)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64Delta);
                _stdin.Write(bytes);
                _stdin.Flush();
            }
            catch
            {
                // Player may have exited early
            }
        }

        public void Finish()
        {
            try
            {
                _stdin.Close();
                _process.WaitForExit(10_000);
            }
            catch
            {
                // Best effort
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static (string? fileName, string? args) FindStreamingPlayer()
        {
            // All players receive raw signed 16-bit LE PCM at 24kHz mono on stdin
            if (OperatingSystem.IsMacOS())
            {
                // sox play supports stdin
                if (ExistsOnPath("play"))
                    return ("play", "-t raw -r 24000 -e signed -b 16 -c 1 -");
                // ffplay as fallback
                if (ExistsOnPath("ffplay"))
                    return ("ffplay", "-f s16le -ar 24000 -ac 1 -nodisp -autoexit -loglevel quiet -i pipe:0");
            }
            else if (OperatingSystem.IsLinux())
            {
                if (ExistsOnPath("aplay"))
                    return ("aplay", "-f S16_LE -r 24000 -c 1 -t raw -q");
                if (ExistsOnPath("play"))
                    return ("play", "-t raw -r 24000 -e signed -b 16 -c 1 -");
                if (ExistsOnPath("ffplay"))
                    return ("ffplay", "-f s16le -ar 24000 -ac 1 -nodisp -autoexit -loglevel quiet -i pipe:0");
            }
            else if (OperatingSystem.IsWindows())
            {
                if (ExistsOnPath("ffplay"))
                    return ("ffplay", "-f s16le -ar 24000 -ac 1 -nodisp -autoexit -loglevel quiet -i pipe:0");
            }

            return (null, null);
        }

        private static bool ExistsOnPath(string command)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    OperatingSystem.IsWindows() ? "where" : "which", command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                var p = Process.Start(psi);
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void WriteUsage(CompletionUsage? usage)
    {
        if (usage is null) return;
        System.Console.WriteLine(
            $"{Dim}[{usage.Input} in / {usage.Output} out | ${usage.Cost.Total:F6}]{Reset}");
        System.Console.WriteLine();
    }
}
