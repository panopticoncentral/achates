using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Achates.Console;

internal sealed class ChatSession(ClientWebSocket ws)
{
    private readonly MarkdownRenderer _md = new();

    public bool ShowThinking { get; set; }

    public async Task SendAndRenderAsync(string text, CancellationToken ct)
    {
        AnsiConsole.WriteLine();

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            await ReceiveResponseAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
    }

    private async Task ReceiveResponseAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var inMessage = false;
        var inThinking = false;

        _md.Reset();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                AnsiConsole.MarkupLine($"[dim yellow]Unknown event: {json.EscapeMarkup()}[/]");
                continue;
            }

            var type = typeProp.GetString();

            switch (type)
            {
                case "thinking.delta":
                    if (!inThinking)
                    {
                        if (ShowThinking)
                        {
                            AnsiConsole.MarkupLine("[dim italic]Thinking[/]");
                        }
                        else
                        {
                            AnsiConsole.Markup("[dim italic]thinking...[/]");
                        }

                        inThinking = true;
                    }
                    if (ShowThinking)
                    {
                        var delta = root.TryGetProperty("delta", out var td) ? td.GetString() ?? "" : "";
                        AnsiConsole.Markup($"[dim]{delta.EscapeMarkup()}[/]");
                    }
                    break;

                case "thinking.end":
                    if (inThinking)
                    {
                        if (ShowThinking)
                        {
                            AnsiConsole.WriteLine();
                            AnsiConsole.Write(new Rule().RuleStyle("dim"));
                        }
                        else
                        {
                            AnsiConsole.Write("\r");
                            AnsiConsole.Write(new string(' ', 20));
                            AnsiConsole.Write("\r");
                        }
                        inThinking = false;
                    }
                    break;

                case "text.delta":
                    if (inThinking && !ShowThinking)
                    {
                        AnsiConsole.Write("\r");
                        AnsiConsole.Write(new string(' ', 20));
                        AnsiConsole.Write("\r");
                        inThinking = false;
                    }
                    if (!inMessage)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[bold cyan]Achates[/]");
                        inMessage = true;
                    }
                    _md.Write(root.GetProperty("delta").GetString() ?? "");
                    break;

                case "text.end":
                    _md.Flush();
                    break;

                case "tool.start":
                {
                    var tool = root.TryGetProperty("tool", out var t) ? t.GetString() : "unknown";
                    var argsStr = "";
                    if (root.TryGetProperty("arguments", out var arguments) &&
                        arguments.ValueKind == JsonValueKind.Object)
                    {
                        argsStr = string.Join(", ",
                            arguments.EnumerateObject().Select(p => $"{p.Name}={p.Value}"));
                    }
                    AnsiConsole.MarkupLine($"  [dim]> {tool.EscapeMarkup()}({argsStr.EscapeMarkup()})[/]");
                    break;
                }

                case "tool.end":
                {
                    var toolResult = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                    var isError = root.TryGetProperty("error", out var e) && e.GetBoolean();
                    if (isError)
                    {
                        AnsiConsole.MarkupLine($"  [red]{toolResult.EscapeMarkup()}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [dim]{Truncate(toolResult, 200).EscapeMarkup()}[/]");
                    }

                    break;
                }

                case "message.end":
                    inMessage = false;
                    if (root.TryGetProperty("usage", out var usage) &&
                        usage.ValueKind == JsonValueKind.Object &&
                        usage.TryGetProperty("input", out var inputProp) &&
                        usage.TryGetProperty("output", out var outputProp) &&
                        usage.TryGetProperty("cost", out var costProp))
                    {
                        AnsiConsole.MarkupLine($"[dim]{inputProp.GetInt32()} in / {outputProp.GetInt32()} out · ${costProp.GetDecimal():F4}[/]");
                    }
                    break;

                case "done":
                    return;
            }
        }
    }

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}
