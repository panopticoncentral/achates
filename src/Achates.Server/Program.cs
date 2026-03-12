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

// --- WebSocket ---
app.Map("/ws", async (HttpContext context, GatewayService gatewayService) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var channel = context.Request.Query["channel"].FirstOrDefault() ?? "console";
    var peer = context.Request.Query["peer"].FirstOrDefault();

    var transport = gatewayService.GetWebSocketTransport(channel);
    if (transport is null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"No WebSocket channel named '{channel}'.");
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    await transport.AcceptAsync(ws, peer, context.RequestAborted);
});

// --- Blazor admin console ---
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
