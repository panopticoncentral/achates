using Achates.Server;
using Achates.Server.Speech;

var builder = WebApplication.CreateBuilder(args);

// Load user config (~/.achates/config.yaml)
var userConfig = ConfigLoader.Load();

builder.Services.AddSingleton(userConfig);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayService>());

// Speech (conditional on tools.speech being configured). The Kokoro server
// itself is managed externally — Achates only sends requests at the
// configured endpoint.
if (userConfig.Tools?.Speech is { } speechConfig)
{
    builder.Services.AddSingleton(speechConfig);
    builder.Services.AddHttpClient("kokoro").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<ISpeechSynthesizer>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("kokoro");
        var endpoint = !string.IsNullOrWhiteSpace(speechConfig.Endpoint)
            ? new Uri(speechConfig.Endpoint)
            : new Uri("http://127.0.0.1:8880"); // Kokoro-FastAPI's own default.
        return new KokoroSpeechSynthesizer(http, endpoint);
    });
}

var app = builder.Build();
app.UseWebSockets();

// --- Health check ---
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Withings OAuth callback ---
app.MapGet("/withings/callback", async (HttpContext context, GatewayService gatewayService) =>
{
    var code = context.Request.Query["code"].FirstOrDefault();
    if (string.IsNullOrEmpty(code))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing authorization code.");
        return;
    }

    var client = gatewayService.WithingsClient;
    if (client is null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Withings is not configured.");
        return;
    }

    await client.ExchangeCodeAsync(code);
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync("""
        <html><body style="font-family: system-ui; text-align: center; padding: 60px;">
        <h1>Connected!</h1>
        <p>Withings account linked successfully. You can close this tab.</p>
        </body></html>
        """);
});

// --- Agent images ---
app.MapGet("/agents/{agentName}/images/{fileName}", (string agentName, string fileName) =>
{
    // Sanitize path components to prevent directory traversal
    if (agentName.Contains("..") || fileName.Contains(".."))
        return Results.BadRequest();

    var filePath = Path.Combine(ConfigLoader.DefaultConfigDir, "agents", agentName, "images", fileName);
    if (!File.Exists(filePath))
        return Results.NotFound();

    return Results.File(filePath, "image/jpeg");
});

// --- WebSocket ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var mobileTransport = gatewayService.MobileTransport;
    if (mobileTransport is null)
    {
        context.Response.StatusCode = 503;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await mobileTransport.HandleConnectionAsync(ws, context.RequestAborted);
});

app.Run();
return 0;
