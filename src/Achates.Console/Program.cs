using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

const string DefaultUrl = "ws://localhost:5000/ws";

var url = GetOption(args, "--url") ?? DefaultUrl;
var channel = GetOption(args, "--channel") ?? "console";
var peer = GetOption(args, "--peer") ?? "local";

if (args is ["help" or "--help" or "-h", ..])
{
    PrintUsage();
    return 0;
}

var wsUrl = $"{url}?channel={Uri.EscapeDataString(channel)}&peer={Uri.EscapeDataString(peer)}";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var ws = new ClientWebSocket();

try
{
    await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to connect to {url}: {ex.Message}");
    Console.Error.WriteLine("Is the server running? Start it with: dotnet run --project src/Achates.Server -- --model <id>");
    return 1;
}

WriteHeader(url, channel, peer);

// Read events from server in background
var receiveTask = Task.Run(async () =>
{
    var buffer = new byte[8192];
    try
    {
        while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            RenderEvent(json);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}, cts.Token);

// Send user input
try
{
    while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
    {
        WritePrompt();
        var input = await Task.Run(Console.ReadLine, cts.Token);

        if (input is null || string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
            break;

        if (string.IsNullOrWhiteSpace(input))
            continue;

        var bytes = Encoding.UTF8.GetBytes(input);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    }
}
catch (OperationCanceledException) { }

if (ws.State == WebSocketState.Open)
{
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
}

await receiveTask;
return 0;

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

static void RenderEvent(string json)
{
    const string Reset = "\x1b[0m";
    const string Dim = "\x1b[2m";
    const string Yellow = "\x1b[33m";
    const string Green = "\x1b[32m";
    const string Red = "\x1b[31m";

    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "text.delta":
                Console.Write(root.GetProperty("delta").GetString());
                break;

            case "text.end":
                Console.WriteLine();
                break;

            case "thinking.delta":
                Console.Write($"{Dim}{root.GetProperty("delta").GetString()}{Reset}");
                break;

            case "tool.start":
                var tool = root.GetProperty("tool").GetString();
                var arguments = root.GetProperty("arguments");
                var argsStr = string.Join(", ",
                    arguments.EnumerateObject().Select(p => $"{p.Name}={p.Value}"));
                Console.WriteLine($"{Yellow}  {tool}({argsStr}){Reset}");
                break;

            case "tool.end":
                var result = root.GetProperty("result").GetString();
                var isError = root.GetProperty("error").GetBoolean();
                var color = isError ? Red : Green;
                Console.WriteLine($"{color}  → {result}{Reset}");
                Console.WriteLine();
                break;

            case "message.end":
                if (root.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind != JsonValueKind.Null)
                {
                    var input = usage.GetProperty("input").GetInt32();
                    var output = usage.GetProperty("output").GetInt32();
                    var cost = usage.GetProperty("cost").GetDecimal();
                    Console.WriteLine($"{Dim}[{input} in / {output} out | ${cost:F6}]{Reset}");
                    Console.WriteLine();
                }
                break;
        }
    }
    catch
    {
        // Malformed event — ignore
    }
}

static void WritePrompt()
{
    Console.Write("\x1b[1m\x1b[36m> \x1b[0m");
}

static void WriteHeader(string url, string channel, string peer)
{
    const string Bold = "\x1b[1m";
    const string Dim = "\x1b[2m";
    const string Reset = "\x1b[0m";

    Console.WriteLine($"{Bold}Achates Console{Reset}");
    Console.WriteLine($"Connected to {url}");
    Console.WriteLine($"Session: {channel}:{peer}");
    Console.WriteLine($"Type {Dim}/exit{Reset} to quit.");
    Console.WriteLine();
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: achates [options]

        Connects to a running Achates server via WebSocket.

        Options:
          --url <ws-url>       Server WebSocket URL (default: ws://localhost:5000/ws)
          --channel <id>       Channel identifier (default: console)
          --peer <id>          Peer identifier (default: local)
        """);
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}
