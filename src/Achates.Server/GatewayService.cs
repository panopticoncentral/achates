using Achates.Agent.Tools;
using Achates.Transports;
using Achates.Configuration;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Cron;
using Achates.Server.Graph;
using Achates.Server.Tools;
using Achates.Server.Withings;

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
    private CronService? _cronService;
    private FileSessionStore? _sessionStore;
    private readonly Dictionary<string, WebSocketTransport> _webSocketTransports = new();
    private WithingsClient? _withingsClient;

    public Gateway Gateway =>
        _gateway ?? throw new InvalidOperationException("Gateway has not started yet.");

    public FileSessionStore SessionStore =>
        _sessionStore ?? throw new InvalidOperationException("Gateway has not started yet.");

    /// <summary>
    /// Get the WebSocket transport for a given channel name.
    /// </summary>
    public WithingsClient? WithingsClient => _withingsClient;

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
        _sessionStore = new FileSessionStore(Path.Combine(achatesHome, "sessions"));
        var sessionStore = _sessionStore;

        // Resolve agents
        var agents = new Dictionary<string, AgentDefinition>();
        foreach (var (name, agentConfig) in config.Agents)
        {
            var model = await ResolveModelAsync(
                agentConfig.Provider ?? config.Provider,
                agentConfig.Model,
                cancellationToken);

            var graphClients = CreateGraphClients(agentConfig.Graph);
            var withingsClient = CreateWithingsClient(agentConfig.Withings);
            if (withingsClient is not null)
                _withingsClient = withingsClient;
            var tools = ResolveTools(agentConfig, model, graphClients, withingsClient);
            var hasTools = agentConfig.Tools ?? [];
            var graphAccountNames = graphClients.Keys.ToList();
            var systemPrompt = SystemPrompt.Build(agentConfig.Description, agentConfig.Prompt, tools,
                hasTodo: agentConfig.TodoFile is not null,
                hasNotes: hasTools.Contains("notes"),
                notesFolderName: ResolveNotesFolder(agentConfig),
                hasMail: hasTools.Contains("mail"),
                hasCalendar: hasTools.Contains("calendar"),
                graphAccountNames: graphAccountNames,
                hasWebSearch: hasTools.Contains("web_search"),
                hasWebFetch: hasTools.Contains("web_fetch"),
                hasCost: hasTools.Contains("cost"),
                hasIMessage: hasTools.Contains("imessage"),
                hasCron: hasTools.Contains("cron"),
                hasHealth: hasTools.Contains("health"));
            var memoryPath = Path.Combine(achatesHome, "agents", name, "memory.md");
            var costLedgerPath = Path.Combine(achatesHome, "agents", name, "costs.jsonl");
            var costLedger = new CostLedger(costLedgerPath);
            var cronStorePath = Path.Combine(achatesHome, "agents", name, "cron.json");
            var cronStore = hasTools.Contains("cron") ? new CronStore(cronStorePath) : null;

            agents[name] = new AgentDefinition
            {
                Model = model,
                SystemPrompt = systemPrompt,
                Tools = tools,
                CompletionOptions = BuildCompletionOptions(agentConfig.Completion, model),
                MemoryPath = memoryPath,
                TodoPath = ExpandHome(agentConfig.TodoFile),
                CostLedger = costLedger,
                CronStore = cronStore,
                GraphClients = graphClients,
            };

            logger.LogInformation("Agent '{Name}' resolved with model {Model}", name, model.Id);

            // Eagerly authenticate Graph so device code prompt appears at startup
            foreach (var (accountName, client) in graphClients)
            {
                try
                {
                    await client.EnsureAuthenticatedAsync(cancellationToken);
                    logger.LogInformation("Agent '{Name}' graph account '{Account}' authenticated",
                        name, accountName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Agent '{Name}' graph account '{Account}' authentication failed — will retry on first use",
                        name, accountName);
                }
            }
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

        // Start cron service for agents that have scheduled tasks enabled
        var cronAgents = agents
            .Where(a => a.Value.CronStore is not null)
            .ToDictionary(a => a.Key, a => (a.Value.CronStore!, a.Value));
        if (cronAgents.Count > 0)
        {
            _cronService = new CronService(cronAgents, bindings,
                loggerFactory.CreateLogger<CronService>());
            _gateway.CronService = _cronService;
            await _cronService.StartAsync(cancellationToken);
        }

        logger.LogInformation("Gateway started with {AgentCount} agent(s) and {ChannelCount} channel(s)",
            agents.Count, bindings.Count);
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_cronService is not null)
        {
            await _cronService.DisposeAsync();
        }
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

    private IReadOnlyList<AgentTool> ResolveTools(AgentConfig agentConfig, Model model,
        IReadOnlyDictionary<string, GraphClient> graphClients, WithingsClient? withingsClient)
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
                case "todo":
                    // TodoTool is added per-session in Gateway.BuildSessionTools
                    break;
                case "cost":
                    // CostTool is added per-session in Gateway.BuildSessionTools
                    break;
                case "cron":
                    // CronTool is added per-session in Gateway.BuildSessionTools
                    break;
                case "mail":
                    if (graphClients.Count == 0)
                        throw new InvalidOperationException("Mail tool requires graph configuration.");
                    tools.Add(new MailTool(graphClients));
                    break;
                case "notes":
                    tools.Add(new NotesTool(ResolveNotesFolder(agentConfig)));
                    break;
                case "calendar":
                    if (graphClients.Count == 0)
                        throw new InvalidOperationException("Calendar tool requires graph configuration.");
                    tools.Add(new CalendarTool(graphClients));
                    break;
                case "web_search":
                    var braveKey = agentConfig.Web?.BraveApiKey
                        ?? Environment.GetEnvironmentVariable("BRAVE_API_KEY")
                        ?? throw new InvalidOperationException(
                            "web_search requires brave_api_key in config or BRAVE_API_KEY env var.");
                    tools.Add(new WebSearchTool(braveKey, httpClientFactory.CreateClient("brave")));
                    break;
                case "web_fetch":
                    tools.Add(new WebFetchTool(httpClientFactory.CreateClient("web")));
                    break;
                case "imessage":
                    var messagesDb = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Messages", "chat.db");
                    tools.Add(new IMessageTool(messagesDb));
                    break;
                case "health":
                    if (withingsClient is null)
                        throw new InvalidOperationException("Health tool requires withings configuration.");
                    tools.Add(new HealthTool(withingsClient));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown tool '{toolName}'.");
            }
        }
        return tools;
    }

    private Dictionary<string, GraphClient> CreateGraphClients(Dictionary<string, GraphConfig>? graphConfigs)
    {
        var clients = new Dictionary<string, GraphClient>();
        if (graphConfigs is null)
            return clients;

        foreach (var (name, graphConfig) in graphConfigs)
        {
            if (graphConfig.ClientId is null)
                continue;
            clients[name] = new GraphClient(graphConfig, httpClientFactory.CreateClient("graph"),
                loggerFactory.CreateLogger<GraphClient>());
        }

        return clients;
    }

    private WithingsClient? CreateWithingsClient(WithingsConfig? withingsConfig)
    {
        if (withingsConfig?.ClientId is null)
            return null;

        return new WithingsClient(withingsConfig, httpClientFactory.CreateClient("withings"),
            loggerFactory.CreateLogger<WithingsClient>());
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

    private static string? ExpandHome(string? path) =>
        path is not null && path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string ResolveNotesFolder(AgentConfig agentConfig) =>
        string.IsNullOrWhiteSpace(agentConfig.Notes?.Folder) ? "Achates" : agentConfig.Notes.Folder;

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
