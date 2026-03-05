using Achates.Console.Tools;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;

namespace Achates.Console;

internal static class ChatCommand
{
    private const int MaxToolRounds = 10;

    public static async Task<int> RunAsync(
        Model model,
        string? systemPrompt,
        double? temperature)
    {
        var messages = new List<CompletionMessage>();
        var toolRegistry = new ChatToolRegistry();
        var tools = model.Parameters.HasFlag(ModelParameters.Tools)
            ? toolRegistry.GetToolDefinitions()
            : null;
        var options = new CompletionOptions
        {
            Temperature = temperature,
            ReasoningEffort = model.Parameters.HasFlag(ModelParameters.ReasoningEffort) ? "medium" : null,
        };

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        ChatRenderer.WriteHeader(model, tools);

        while (!cts.Token.IsCancellationRequested)
        {
            ChatRenderer.WritePrompt();
            var input = System.Console.ReadLine();

            if (input is null ||
                string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
                continue;

            messages.Add(BuildUserMessage(input));

            try
            {
                await RunCompletionLoopAsync(
                    model, messages, systemPrompt, tools, options, toolRegistry, cts.Token);
            }
            catch (OperationCanceledException)
            {
                System.Console.WriteLine();
                break;
            }
        }

        return 0;
    }

    private static async Task RunCompletionLoopAsync(
        Model model,
        List<CompletionMessage> messages,
        string? systemPrompt,
        IReadOnlyList<CompletionTool>? tools,
        CompletionOptions options,
        ChatToolRegistry toolRegistry,
        CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var context = new CompletionContext
            {
                SystemPrompt = systemPrompt,
                Messages = messages,
                Tools = tools,
            };

            var stream = model.Provider.GetCompletions(model, context, options, cancellationToken);
            var result = await ChatRenderer.RenderStreamAsync(stream, cancellationToken);
            messages.Add(result);

            if (result.CompletionStopReason != CompletionStopReason.ToolUse)
            {
                ChatRenderer.WriteUsage(result.CompletionUsage);
                return;
            }

            // Execute tool calls and append results
            var toolCalls = result.Content.OfType<CompletionToolCall>().ToList();
            foreach (var toolCall in toolCalls)
            {
                ChatRenderer.WriteToolExecution(toolCall);
                var toolResult = toolRegistry.Execute(toolCall);
                ChatRenderer.WriteToolResult(toolResult);
                messages.Add(toolResult);
            }
        }

        System.Console.Error.WriteLine("Warning: Maximum tool rounds reached.");
    }

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private static readonly Dictionary<string, string> FileMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".md"] = "text/markdown",
    };

    private static CompletionUserMessage BuildUserMessage(string input)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Check for /image <path> [text] command
        if (input.StartsWith("/image ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input["/image ".Length..].Trim();
            var (path, text) = SplitPathAndText(rest);

            if (TryLoadImage(path, out var imageContent))
            {
                var content = new List<CompletionUserContent> { imageContent };
                if (!string.IsNullOrWhiteSpace(text))
                    content.Add(new CompletionTextContent { Text = text });

                return new CompletionUserContentMessage
                {
                    Content = content,
                    Timestamp = timestamp,
                };
            }

            // Fall through to plain text if image load failed
        }

        // Check for /file <path> [text] command
        if (input.StartsWith("/file ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input["/file ".Length..].Trim();
            var (path, text) = SplitPathAndText(rest);

            if (TryLoadFile(path, out var fileContent))
            {
                var content = new List<CompletionUserContent> { fileContent };
                if (!string.IsNullOrWhiteSpace(text))
                    content.Add(new CompletionTextContent { Text = text });

                return new CompletionUserContentMessage
                {
                    Content = content,
                    Timestamp = timestamp,
                };
            }

            // Fall through to plain text if file load failed
        }

        return new CompletionUserTextMessage
        {
            Text = input,
            Timestamp = timestamp,
        };
    }

    private static (string path, string? text) SplitPathAndText(string input)
    {
        // Handle quoted paths: "/path/with spaces/img.png" describe this
        if (input.StartsWith('"'))
        {
            var endQuote = input.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var path = input[1..endQuote];
                var text = input[(endQuote + 1)..].Trim();
                return (path, text.Length > 0 ? text : null);
            }
        }

        // Unquoted: split on first space after the path
        var spaceIdx = input.IndexOf(' ');
        if (spaceIdx < 0)
            return (input, null);

        return (input[..spaceIdx], input[(spaceIdx + 1)..].Trim());
    }

    private static bool TryLoadImage(string path, out CompletionImageContent imageContent)
    {
        imageContent = null!;
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            System.Console.Error.WriteLine($"File not found: {fullPath}");
            return false;
        }

        var ext = Path.GetExtension(fullPath);
        if (!ImageMimeTypes.TryGetValue(ext, out var mime))
        {
            System.Console.Error.WriteLine($"Unsupported image format: {ext}");
            return false;
        }

        var data = Convert.ToBase64String(File.ReadAllBytes(fullPath));
        imageContent = new CompletionImageContent { Data = data, MimeType = mime };
        return true;
    }

    private static bool TryLoadFile(string path, out CompletionFileContent fileContent)
    {
        fileContent = null!;
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            System.Console.Error.WriteLine($"File not found: {fullPath}");
            return false;
        }

        var ext = Path.GetExtension(fullPath);
        if (!FileMimeTypes.TryGetValue(ext, out var mime))
            mime = "application/octet-stream";

        var data = Convert.ToBase64String(File.ReadAllBytes(fullPath));
        fileContent = new CompletionFileContent
        {
            Data = data,
            MimeType = mime,
            FileName = Path.GetFileName(fullPath),
        };
        return true;
    }
}
