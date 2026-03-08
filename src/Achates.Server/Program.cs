using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Configuration;
using Achates.Server;

var builder = WebApplication.CreateBuilder(args);

// Load user config (~/.achates/config.yaml)
var userConfig = ConfigLoader.Load();

if (string.IsNullOrEmpty(userConfig.Model))
{
    Console.Error.WriteLine("Error: Model is required. Set model in ~/.achates/config.yaml.");
    return 1;
}

builder.Services.AddSingleton(userConfig);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayService>());

var app = builder.Build();
app.UseWebSockets();

// --- Health check ---
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- WebSocket: stream events in real time ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    var agent = gatewayService.Gateway.Agent;
    var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

    // Read messages from the client, stream events back
    var buffer = new byte[4096];
    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            break;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        if (string.IsNullOrWhiteSpace(text))
        {
            continue;
        }

        var stream = agent.PromptAsync(text);

        await foreach (var evt in stream.WithCancellation(cts.Token))
        {
            var json = SerializeEvent(evt);
            if (json is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
            }
        }
    }

    if (ws.State == WebSocketState.Open)
    {
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
});

app.Run();
return 0;

static string? SerializeEvent(AgentEvent evt)
{
    return evt switch
    {
        MessageStreamEvent { Inner: CompletionTextDeltaEvent e } =>
            JsonSerializer.Serialize(new { type = "text.delta", delta = e.Delta }),
        MessageStreamEvent { Inner: CompletionTextEndEvent } =>
            JsonSerializer.Serialize(new { type = "text.end" }),
        MessageStreamEvent { Inner: CompletionThinkingDeltaEvent e } =>
            JsonSerializer.Serialize(new { type = "thinking.delta", delta = e.Delta }),
        MessageEndEvent { Message: AssistantMessage { StopReason: not CompletionStopReason.ToolUse } a } =>
            JsonSerializer.Serialize(new
            {
                type = "message.end",
                usage = a.Usage is { } u ? new { input = u.Input, output = u.Output, cost = u.Cost.Total } : null,
            }),
        ToolStartEvent e =>
            JsonSerializer.Serialize(new { type = "tool.start", tool = e.ToolName, arguments = e.Arguments }),
        ToolEndEvent e =>
            JsonSerializer.Serialize(new
            {
                type = "tool.end",
                tool = e.ToolName,
                result = string.Join("\n", e.Result.Content.OfType<CompletionTextContent>().Select(c => c.Text)),
                error = e.IsError,
            }),
        AgentEndEvent =>
            JsonSerializer.Serialize(new { type = "done" }),
        _ => null,
    };
}

