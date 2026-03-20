using Achates.Agent.Tools;
using Achates.Providers;
using Achates.Providers.Completions;
using Achates.Providers.Models;
using Achates.Server.Cron;
using Achates.Server.Graph;
using Achates.Server.Mobile;
using Achates.Server.Tools;
using Achates.Server.Withings;

namespace Achates.Server;

/// <summary>
/// Hosted service that resolves agents from config, creates the mobile transport,
/// and manages cron jobs.
/// </summary>
public sealed class GatewayService(
    AchatesConfig config,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ILogger<GatewayService> logger)
    : IHostedLifecycleService, IAsyncDisposable
{
    private Dictionary<string, AgentDefinition> _agents = new();
    private CronService? _cronService;
    private WithingsClient? _withingsClient;
    private MobileTransport? _mobileTransport;
    private MobileSessionStore? _mobileSessionStore;
    private readonly DeviceCommandBridge _deviceBridge = new();

    public IReadOnlyDictionary<string, AgentDefinition> Agents => _agents;
    public MobileTransport? MobileTransport => _mobileTransport;
    public MobileSessionStore? MobileSessionStore => _mobileSessionStore;
    public WithingsClient? WithingsClient => _withingsClient;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var achatesHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates");

        var agentConfigs = AgentLoader.LoadAgents(achatesHome);
        if (agentConfigs.Count == 0)
        {
            AgentLoader.CreateDefault(achatesHome);
            agentConfigs = AgentLoader.LoadAgents(achatesHome);
        }

        if (agentConfigs.Count == 0)
            throw new InvalidOperationException(
                "No agents found. Create AGENT.md in ~/.achates/agents/{name}/");

        var agents = new Dictionary<string, AgentDefinition>();

        foreach (var (name, agentConfig) in agentConfigs)
        {
            var model = await ResolveModelAsync(
                agentConfig.Provider ?? config.Provider?.Name,
                agentConfig.Model,
                cancellationToken);

            var prompt = ResolvePrompt(agentConfig);

            var toolsConfig = config.Tools;
            var graphClients = CreateGraphClients(toolsConfig?.Graph);
            var withingsClient = CreateWithingsClient(toolsConfig?.Withings);
            if (withingsClient is not null)
                _withingsClient = withingsClient;

            // Resolve transcription model if any agent uses the transcribe tool
            Model? transcribeModel = null;
            if (agentConfig.Tools?.Contains("transcribe") == true)
            {
                var transcribeModelId = toolsConfig?.Transcribe?.Model ?? "google/gemini-2.5-flash";
                transcribeModel = await ResolveModelAsync(
                    agentConfig.Provider ?? config.Provider?.Name,
                    transcribeModelId,
                    cancellationToken);
                logger.LogInformation("Transcribe model resolved: {Model}", transcribeModel.Id);
            }

            var tools = ResolveTools(agentConfig, toolsConfig, model, graphClients, withingsClient,
                transcribeModel);
            var hasTools = agentConfig.Tools ?? [];
            var graphAccountNames = graphClients.Keys.ToList();
            var systemPrompt = SystemPrompt.Build(agentConfig.Description, prompt, tools,
                hasTodo: toolsConfig?.Todo?.File is not null,
                hasNotes: hasTools.Contains("notes"),
                notesFolderName: ResolveNotesFolder(toolsConfig),
                hasMail: hasTools.Contains("mail"),
                hasCalendar: hasTools.Contains("calendar"),
                graphAccountNames: graphAccountNames,
                hasWebSearch: hasTools.Contains("web_search"),
                hasWebFetch: hasTools.Contains("web_fetch"),
                hasCost: hasTools.Contains("cost"),
                hasIMessage: hasTools.Contains("imessage"),
                hasCron: hasTools.Contains("cron"),
                hasHealth: hasTools.Contains("health"),
                hasTranscribe: hasTools.Contains("transcribe"),
                hasChat: hasTools.Contains("chat"),
                chatAgentNames: agentConfig.AllowChat);
            var memoryPath = Path.Combine(achatesHome, "agents", name, "memory.md");
            var costLedgerPath = Path.Combine(achatesHome, "agents", name, "costs.jsonl");
            var costLedger = new CostLedger(costLedgerPath);
            var cronStorePath = Path.Combine(achatesHome, "agents", name, "cron.json");
            var cronStore = hasTools.Contains("cron") ? new CronStore(cronStorePath) : null;

            var agentDef = new AgentDefinition
            {
                Model = model,
                SystemPrompt = systemPrompt,
                Tools = tools,
                CompletionOptions = BuildCompletionOptions(agentConfig.Completion, model),
                MemoryPath = memoryPath,
                Description = agentConfig.Description,
                TodoPath = ExpandHome(toolsConfig?.Todo?.File),
                CostLedger = costLedger,
                CronStore = cronStore,
                GraphClients = graphClients,
            };

            agents[name] = agentDef;
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

        _agents = agents;

        // Create MobileTransport
        _mobileSessionStore = new MobileSessionStore(achatesHome);
        _mobileTransport = new MobileTransport(agents, _mobileSessionStore, loggerFactory);
        _deviceBridge.SetTransport(_mobileTransport);

        // Start cron service for agents that have scheduled tasks enabled
        var cronAgents = agents
            .Where(a => a.Value.CronStore is not null)
            .ToDictionary(a => a.Key, a => (a.Value.CronStore!, a.Value));
        if (cronAgents.Count > 0)
        {
            _cronService = new CronService(cronAgents, _mobileTransport, _mobileSessionStore,
                loggerFactory.CreateLogger<CronService>());
            _mobileTransport.CronService = _cronService;
            await _cronService.StartAsync(cancellationToken);
        }

        logger.LogInformation("Started with {AgentCount} agent(s)", agents.Count);
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
    }

    private IReadOnlyList<AgentTool> ResolveTools(AgentConfig agentConfig, ToolsConfig? toolsConfig,
        Model model, IReadOnlyDictionary<string, GraphClient> graphClients, WithingsClient? withingsClient,
        Model? transcribeModel = null)
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
                case "todo":
                case "cost":
                case "cron":
                case "chat":
                    // Per-session tools — added in MobileTransport.CreateRuntime
                    break;
                case "mail":
                    if (graphClients.Count == 0)
                        throw new InvalidOperationException("Mail tool requires graph configuration.");
                    tools.Add(new MailTool(graphClients));
                    break;
                case "notes":
                    tools.Add(new NotesTool(ResolveNotesFolder(toolsConfig)));
                    break;
                case "calendar":
                    if (graphClients.Count == 0)
                        throw new InvalidOperationException("Calendar tool requires graph configuration.");
                    tools.Add(new CalendarTool(graphClients));
                    break;
                case "web_search":
                    var braveKey = toolsConfig?.WebSearch?.BraveApiKey
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
                    tools.Add(new IMessageTool(messagesDb, new ContactResolver(graphClients)));
                    break;
                case "health":
                    if (withingsClient is null)
                        throw new InvalidOperationException("Health tool requires withings configuration.");
                    tools.Add(new HealthTool(withingsClient));
                    break;
                case "transcribe":
                    if (transcribeModel is null)
                        throw new InvalidOperationException(
                            "Transcribe tool requires a transcribe model. Set tools.transcribe.model in config.");
                    tools.Add(new TranscribeTool(transcribeModel));
                    break;
                case "location":
                    tools.Add(new LocationTool(_deviceBridge));
                    break;
                case "camera":
                    tools.Add(new CameraTool(_deviceBridge));
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

    private static string? ResolvePrompt(AgentConfig agentConfig) => agentConfig.Prompt;

    private static string? ExpandHome(string? path) =>
        path is not null && path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string ResolveNotesFolder(ToolsConfig? toolsConfig) =>
        string.IsNullOrWhiteSpace(toolsConfig?.Notes?.Folder) ? "Achates" : toolsConfig.Notes.Folder;

    private async Task<Model> ResolveModelAsync(string? providerId, string? modelId, CancellationToken cancellationToken)
    {
        providerId ??= config.Provider?.Name
            ?? throw new InvalidOperationException("No provider specified.");
        if (modelId is null)
            throw new InvalidOperationException("No model specified.");

        var provider = ModelProviders.Create(providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

        var apiKey = config.Provider?.ApiKey
            ?? Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException(
                $"API key not found. Set api_key in provider config or the {provider.EnvironmentKey} environment variable.");

        provider.Key = apiKey;
        provider.HttpClient = httpClientFactory.CreateClient("achates");

        var models = await provider.GetModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' not found.");
    }
}
