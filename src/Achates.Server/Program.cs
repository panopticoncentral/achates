using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Achates.Agent.Events;
using Achates.Agent.Messages;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Server;

var builder = WebApplication.CreateBuilder(args);

// Bind config
var serverOptions = new ServerOptions();
builder.Configuration.GetSection("Achates").Bind(serverOptions);

// Allow CLI overrides: --model, --provider, --system
if (builder.Configuration["model"] is { Length: > 0 } model)
    serverOptions.Model = model;
if (builder.Configuration["provider"] is { Length: > 0 } provider)
    serverOptions.Provider = provider;
if (builder.Configuration["system"] is { Length: > 0 } system)
    serverOptions.SystemPrompt = system;

if (string.IsNullOrEmpty(serverOptions.Model))
{
    Console.Error.WriteLine("Error: Model is required. Set Achates:Model in config or pass --model <id>.");
    return 1;
}

builder.Services.AddSingleton(serverOptions);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayService>());

var app = builder.Build();
app.UseWebSockets();

// --- Health check ---
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- REST: send a message and get the complete response ---
app.MapPost("/chat", async (ChatRequest request, GatewayService gatewayService, CancellationToken ct) =>
{
    var gateway = gatewayService.Gateway;
    var key = new SessionKey(request.Channel ?? "api", request.Peer ?? "default");
    var agent = gateway.GetOrCreateSession(key);

    var stream = agent.PromptAsync(request.Message);
    var responseText = "";

    await foreach (var evt in stream.WithCancellation(ct))
    {
        if (evt is MessageStreamEvent { Inner: CompletionTextDeltaEvent delta })
        {
            responseText += delta.Delta;
        }
    }

    return Results.Ok(new ChatResponse(responseText.Trim(), key.ToString()));
});

// --- WebSocket: stream events in real time ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    var key = new SessionKey(
        context.Request.Query["channel"].FirstOrDefault() ?? "ws",
        context.Request.Query["peer"].FirstOrDefault() ?? "default");
    var agent = gatewayService.Gateway.GetOrCreateSession(key);
    var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

    // Read messages from the client, stream events back
    var buffer = new byte[4096];
    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
            break;

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        if (string.IsNullOrWhiteSpace(text))
            continue;

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

// --- Request/response types ---

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
        MessageEndEvent { Message: AssistantMessage a } when a.StopReason is not CompletionStopReason.ToolUse =>
            JsonSerializer.Serialize(new
            {
                type = "message.end",
                usage = a.Usage is { } u ? new { u.Input, u.Output, cost = u.Cost.Total } : null,
            }),
        ToolStartEvent e =>
            JsonSerializer.Serialize(new { type = "tool.start", tool = e.ToolName, e.Arguments }),
        ToolEndEvent e =>
            JsonSerializer.Serialize(new
            {
                type = "tool.end",
                tool = e.ToolName,
                result = string.Join("\n", e.Result.Content.OfType<CompletionTextContent>().Select(c => c.Text)),
                error = e.IsError,
            }),
        _ => null,
    };
}

record ChatRequest(string Message, string? Channel = null, string? Peer = null);
record ChatResponse(string Reply, string Session);
