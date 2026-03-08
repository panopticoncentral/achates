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

// --- WebSocket ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    await gatewayService.WebSocketChannel.AcceptAsync(ws, context.RequestAborted);
});

app.Run();
return 0;

