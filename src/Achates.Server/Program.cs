using Achates.Configuration;
using Achates.Server;
using Achates.Server.Components;

var builder = WebApplication.CreateBuilder(args);

// Load user config (~/.achates/config.yaml)
var userConfig = ConfigLoader.Load();

if (userConfig.Agents is not { Count: > 0 })
{
    Console.Error.WriteLine("Error: No agents configured. Add at least one agent to ~/.achates/config.yaml.");
    return 1;
}

builder.Services.AddSingleton(userConfig);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayService>());
builder.Services.AddSingleton<AdminService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
app.UseWebSockets();
app.UseStaticFiles();
app.UseAntiforgery();

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

// --- WebSocket ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var agent = context.Request.Query["agent"].FirstOrDefault() ?? "default";
    var peer = context.Request.Query["peer"].FirstOrDefault();

    var transport = gatewayService.GetWebSocketTransport(agent);
    if (transport is null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"No WebSocket channel for agent '{agent}'.");
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    await transport.AcceptAsync(ws, peer, context.RequestAborted);
});

// --- Mobile WebSocket (v2 protocol) ---
app.Map("/ws/v2", async (HttpContext context, GatewayService gatewayService) =>
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

    var peer = context.Request.Query["peer"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await mobileTransport.HandleConnectionAsync(ws, peer, context.RequestAborted);
});

// --- Blazor admin console ---
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
