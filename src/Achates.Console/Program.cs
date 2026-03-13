using System.Net.WebSockets;
using Achates.Console;
using Spectre.Console;

var url = GetOption(args, "--url") ?? "ws://localhost:5000/ws";
var agent = GetOption(args, "--agent") ?? "default";
var peer = GetOption(args, "--peer") ?? "local";

if (args is ["help" or "--help" or "-h", ..])
{
    AnsiConsole.WriteLine("""
        Usage: achates [options]

        Connects to a running Achates server via WebSocket.

        Options:
          --url <ws-url>       Server WebSocket URL (default: ws://localhost:5000/ws)
          --agent <name>       Agent name (default: default)
          --peer <id>          Peer identifier (default: local)
          /clear               Clear the screen
          /thinking            Toggle showing thinking content
          /exit                Quit
        """);
    return 0;
}

var wsUrl = $"{url}?agent={Uri.EscapeDataString(agent)}&peer={Uri.EscapeDataString(peer)}";

using var ws = new ClientWebSocket();
try
{
    AnsiConsole.Markup($"[dim]Connecting to {url.EscapeMarkup()}...[/]");
    await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
    AnsiConsole.MarkupLine(" [green]connected.[/]");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($" [red]failed.[/]");
    AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine("[dim]Is the server running? Start with: dotnet run --project src/Achates.Server -- --model <id>[/]");
    return 1;
}

AnsiConsole.Write(new Rule($"[cyan]{agent}:{peer}[/]").LeftJustified().RuleStyle("dim"));
AnsiConsole.WriteLine();

var chat = new ChatSession(ws);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
{
    var input = AnsiConsole.Prompt(
        new TextPrompt<string>("[cyan]>[/]")
            .AllowEmpty());

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (string.Equals(input.Trim(), "/clear", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        continue;
    }

    if (string.Equals(input.Trim(), "/thinking", StringComparison.OrdinalIgnoreCase))
    {
        chat.ShowThinking = !chat.ShowThinking;
        AnsiConsole.MarkupLine(chat.ShowThinking
            ? "[dim]Thinking: [green]visible[/][/]"
            : "[dim]Thinking: [yellow]hidden[/][/]");
        continue;
    }

    await chat.SendAndRenderAsync(input, cts.Token);
}

if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
{
    try
    {
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
    catch (WebSocketException) { }
}

return 0;

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
