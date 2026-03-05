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

    public static void WriteUsage(CompletionUsage? usage)
    {
        if (usage is null) return;
        System.Console.WriteLine(
            $"{Dim}[{usage.Input} in / {usage.Output} out | ${usage.Cost.Total:F6}]{Reset}");
        System.Console.WriteLine();
    }
}
