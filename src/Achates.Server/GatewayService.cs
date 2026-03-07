using Achates.Agent.Tools;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Tools;

namespace Achates.Server;

/// <summary>
/// Hosted service that creates and manages the gateway lifecycle.
/// Resolves the model at startup, creates the gateway, starts all channels.
/// </summary>
public sealed class GatewayService : IHostedLifecycleService, IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GatewayService> _logger;

    private Gateway? _gateway;

    public GatewayService(
        ServerOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<GatewayService> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Gateway Gateway =>
        _gateway ?? throw new InvalidOperationException("Gateway has not started yet.");

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var model = await ResolveModelAsync(cancellationToken);

        AgentTool[] tools = [new TimeTool(), new CalculatorTool(), new WeatherTool()];

        var gatewayOptions = new GatewayOptions
        {
            Model = model,
            SystemPrompt = _options.SystemPrompt,
            Tools = model.Parameters.HasFlag(ModelParameters.Tools) ? tools : null,
            CompletionOptions = new CompletionOptions
            {
                ReasoningEffort = model.Parameters.HasFlag(ModelParameters.ReasoningEffort)
                    ? "medium"
                    : null,
            },
        };

        _gateway = new Gateway(gatewayOptions);
        await _gateway.StartAsync(cancellationToken);

        _logger.LogInformation("Gateway started with model {Model} ({Name})", model.Id, model.Name);
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
        var provider = ModelProviders.Create(_options.Provider)
            ?? throw new InvalidOperationException($"Unknown provider: {_options.Provider}");

        var apiKey = Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException(
                $"API key not found. Set the {provider.EnvironmentKey} environment variable.");

        provider.Key = apiKey;
        provider.HttpClient = _httpClientFactory.CreateClient("achates");

        var models = await provider.GetModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Id.Equals(_options.Model, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{_options.Model}' not found.");
    }
}
