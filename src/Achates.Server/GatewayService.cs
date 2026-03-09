using Achates.Agent.Tools;
using Achates.Transports;
using Achates.Configuration;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Tools;

namespace Achates.Server;

/// <summary>
/// Hosted service that creates and manages the gateway lifecycle.
/// Resolves agents and channels from config, creates the gateway, starts all transports.
/// </summary>
public sealed class GatewayService(
    AchatesConfig config,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ILogger<GatewayService> logger)
    : IHostedLifecycleService, IAsyncDisposable
{
    private Gateway? _gateway;
    private readonly Dictionary<string, WebSocketTransport> _webSocketTransports = new();

    public Gateway Gateway =>
        _gateway ?? throw new InvalidOperationException("Gateway has not started yet.");

    /// <summary>
    /// Get the WebSocket transport for a given channel name.
    /// </summary>
    public WebSocketTransport? GetWebSocketTransport(string channelName) =>
        _webSocketTransports.GetValueOrDefault(channelName);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (config.Agents is not { Count: > 0 })
            throw new InvalidOperationException("No agents configured. Add at least one agent to config.yaml.");
        if (config.Channels is not { Count: > 0 })
            throw new InvalidOperationException("No channels configured. Add at least one channel to config.yaml.");

        var achatesHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates");
        var sessionStore = new FileSessionStore(Path.Combine(achatesHome, "sessions"));

        // Resolve agents
        var agents = new Dictionary<string, AgentDefinition>();
        foreach (var (name, agentConfig) in config.Agents)
        {
            var model = await ResolveModelAsync(
                agentConfig.Provider ?? config.Provider,
                agentConfig.Model,
                cancellationToken);

            var tools = ResolveTools(agentConfig, model);
            var systemPrompt = SystemPrompt.Build(agentConfig.Description, agentConfig.Prompt, tools);
            var memoryPath = Path.Combine(achatesHome, "agents", name, "memory.md");

            agents[name] = new AgentDefinition
            {
                Model = model,
                SystemPrompt = systemPrompt,
                Tools = tools,
                CompletionOptions = BuildCompletionOptions(agentConfig.Completion, model),
                MemoryPath = memoryPath,
            };

            logger.LogInformation("Agent '{Name}' resolved with model {Model}", name, model.Id);
        }

        // Resolve channels
        var bindings = new List<ChannelBinding>();
        foreach (var (channelName, channelConfig) in config.Channels)
        {
            var agentName = channelConfig.Agent
                ?? throw new InvalidOperationException($"Channel '{channelName}' has no agent specified.");

            if (!agents.TryGetValue(agentName, out var agentDef))
                throw new InvalidOperationException(
                    $"Channel '{channelName}' references unknown agent '{agentName}'.");

            var transport = CreateTransport(channelName, channelConfig);

            bindings.Add(new ChannelBinding
            {
                Name = channelName,
                Transport = transport,
                AgentName = agentName,
                Agent = agentDef,
            });

            logger.LogInformation("Channel '{Channel}' bound to agent '{Agent}' via {Transport}",
                channelName, agentName, channelConfig.Transport);
        }

        _gateway = new Gateway(bindings, sessionStore);
        await _gateway.StartAsync(cancellationToken);

        logger.LogInformation("Gateway started with {AgentCount} agent(s) and {ChannelCount} channel(s)",
            agents.Count, bindings.Count);
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

    private ITransport CreateTransport(string channelName, ChannelConfig channelConfig)
    {
        return channelConfig.Transport switch
        {
            "websocket" => CreateWebSocketTransport(channelName),
            "telegram" => CreateTelegramTransport(channelConfig),
            _ => throw new InvalidOperationException(
                $"Unknown transport type '{channelConfig.Transport}' for channel '{channelName}'."),
        };
    }

    private WebSocketTransport CreateWebSocketTransport(string channelName)
    {
        var transport = new WebSocketTransport();
        _webSocketTransports[channelName] = transport;
        return transport;
    }

    private TelegramTransport CreateTelegramTransport(ChannelConfig channelConfig)
    {
        var token = channelConfig.Token
            ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? throw new InvalidOperationException("Telegram channel requires a token.");

        return new TelegramTransport(
            token,
            channelConfig.AllowedChatIds,
            loggerFactory.CreateLogger<TelegramTransport>());
    }

    private static IReadOnlyList<AgentTool> ResolveTools(AgentConfig agentConfig, Model model)
    {
        if (!model.Parameters.HasFlag(ModelParameters.Tools))
            return [];

        var tools = new List<AgentTool>();
        foreach (var toolName in agentConfig.Tools ?? [])
        {
            switch (toolName)
            {
                case "session":
                    tools.Add(new SessionTool(model));
                    break;
                case "memory":
                    // MemoryTool is added per-session in Gateway.BuildSessionTools
                    break;
                default:
                    throw new InvalidOperationException($"Unknown tool '{toolName}'.");
            }
        }
        return tools;
    }

    private static CompletionOptions? BuildCompletionOptions(CompletionConfig? completion, Model model)
    {
        if (completion is null)
            return null;

        return new CompletionOptions
        {
            Temperature = completion.Temperature,
            MaxTokens = completion.MaxTokens,
            ReasoningEffort = model.Parameters.HasFlag(ModelParameters.ReasoningEffort)
                ? completion.ReasoningEffort ?? "medium"
                : null,
        };
    }

    private async Task<Model> ResolveModelAsync(string? providerId, string? modelId, CancellationToken cancellationToken)
    {
        providerId ??= config.Provider
            ?? throw new InvalidOperationException("No provider specified.");
        if (modelId is null)
            throw new InvalidOperationException("No model specified.");

        var provider = ModelProviders.Create(providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

        var apiKey = Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException(
                $"API key not found. Set the {provider.EnvironmentKey} environment variable.");

        provider.Key = apiKey;
        provider.HttpClient = httpClientFactory.CreateClient("achates");

        var models = await provider.GetModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' not found.");
    }
}
