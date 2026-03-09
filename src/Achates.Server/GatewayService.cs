using Achates.Agent.Tools;
using Achates.Channels;
using Achates.Configuration;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Tools;

namespace Achates.Server;

/// <summary>
/// Hosted service that creates and manages the gateway lifecycle.
/// Resolves the model at startup, creates the gateway, starts all channels.
/// </summary>
public sealed class GatewayService(
    AchatesConfig config,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ILogger<GatewayService> logger)
    : IHostedLifecycleService, IAsyncDisposable
{
    private Gateway? _gateway;

    public Gateway Gateway =>
        _gateway ?? throw new InvalidOperationException("Gateway has not started yet.");

    public WebSocketChannel WebSocketChannel { get; } = new();

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var model = await ResolveModelAsync(cancellationToken);

        AgentTool[] tools = [new SessionTool(model)];

        var activeTools = model.Parameters.HasFlag(ModelParameters.Tools) ? tools : null;

        var sessionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates", "sessions");
        var sessionStore = new FileSessionStore(sessionsPath);

        var gatewayOptions = new GatewayOptions
        {
            Model = model,
            SystemPrompt = SystemPrompt.Build(activeTools),
            Tools = activeTools,
            CompletionOptions = new CompletionOptions
            {
                Temperature = config.Completion?.Temperature,
                MaxTokens = config.Completion?.MaxTokens,
                ReasoningEffort = model.Parameters.HasFlag(ModelParameters.ReasoningEffort)
                    ? config.Completion?.ReasoningEffort ?? "medium"
                    : null,
            },
            SessionStore = sessionStore,
        };

        _gateway = new Gateway(gatewayOptions);
        _gateway.AddChannel(WebSocketChannel);

        var telegramToken = config.Telegram?.Token
            ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (!string.IsNullOrEmpty(telegramToken))
        {
            var telegramChannel = new TelegramChannel(
                telegramToken,
                config.Telegram?.AllowedChatIds,
                loggerFactory.CreateLogger<TelegramChannel>());
            _gateway.AddChannel(telegramChannel);
        }

        await _gateway.StartAsync(cancellationToken);

        logger.LogInformation("Gateway started with model {Model} ({Name})", model.Id, model.Name);
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }
    }

    private async Task<Model> ResolveModelAsync(CancellationToken cancellationToken)
    {
        var provider = ModelProviders.Create(config.Provider!)
            ?? throw new InvalidOperationException($"Unknown provider: {config.Provider}");

        var apiKey = Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException(
                $"API key not found. Set the {provider.EnvironmentKey} environment variable.");

        provider.Key = apiKey;
        provider.HttpClient = httpClientFactory.CreateClient("achates");

        var models = await provider.GetModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Id.Equals(config.Model, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{config.Model}' not found.");
    }
}
