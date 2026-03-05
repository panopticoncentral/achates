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

        System.Console.WriteLine($"Type {Dim}/image <path> [text]{Reset} to send an image, {Dim}/file <path> [text]{Reset} to send a file, {Dim}/exit{Reset} to quit.");
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

                case CompletionErrorEvent e:
                    if (inThinking) System.Console.Write(Reset);
                    System.Console.Error.WriteLine($"{Red}Error: {e.Error.ErrorMessage}{Reset}");
                    break;
            }
        }

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

    public static void WriteUsage(CompletionUsage? usage)
    {
        if (usage is null) return;
        System.Console.WriteLine(
            $"{Dim}[{usage.Input} in / {usage.Output} out | ${usage.Cost.Total:F6}]{Reset}");
        System.Console.WriteLine();
    }
}
