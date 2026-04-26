using System.Runtime.CompilerServices;
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
    private string? _achatesHome;
    private Dictionary<string, AgentDefinition> _agents = new();
    private CronService? _cronService;
    private WithingsClient? _withingsClient;
    private MobileTransport? _mobileTransport;
    private MobileSessionStore? _mobileSessionStore;
    private AgentStateCache? _agentStateCache;
    private readonly DeviceCommandBridge _deviceBridge = new();

    /// <summary>
    /// All known tool names that can be assigned to agents.
    /// Derived by scanning all concrete <see cref="AgentTool"/> subclasses in this assembly.
    /// </summary>
    public static IReadOnlyCollection<string> AllToolNames { get; } = new SortedSet<string>(
        typeof(GatewayService).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(AgentTool)) && !t.IsAbstract)
            .Select(t => ((AgentTool)RuntimeHelpers.GetUninitializedObject(t)).Name));

    /// <summary>
    /// All known tools with names and labels, sorted by name.
    /// </summary>
    public static IReadOnlyList<(string Name, string Label)> AllTools { get; } =
        typeof(GatewayService).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(AgentTool)) && !t.IsAbstract)
            .Select(t => (AgentTool)RuntimeHelpers.GetUninitializedObject(t))
            .Select(t => (t.Name, t.Label))
            .OrderBy(t => t.Name)
            .ToList();

    public IReadOnlyDictionary<string, AgentDefinition> Agents => _agents;
    public MobileTransport? MobileTransport => _mobileTransport;
    public MobileSessionStore? MobileSessionStore => _mobileSessionStore;
    public AgentStateCache? AgentStateCache => _agentStateCache;
    public WithingsClient? WithingsClient => _withingsClient;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _achatesHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".achates");

        var agentConfigs = AgentLoader.LoadAgents(_achatesHome);
        if (agentConfigs.Count == 0)
        {
            AgentLoader.CreateDefault(_achatesHome);
            agentConfigs = AgentLoader.LoadAgents(_achatesHome);
        }

        if (agentConfigs.Count == 0)
            throw new InvalidOperationException(
                "No agents found. Create AGENT.md in ~/.achates/agents/{name}/");

        var agents = new Dictionary<string, AgentDefinition>();

        foreach (var (name, agentConfig) in agentConfigs)
        {
            agents[name] = await ResolveAgentAsync(name, agentConfig, cancellationToken);

            // Eagerly authenticate Graph so device code prompt appears at startup
            foreach (var (accountName, client) in agents[name].GraphClients)
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

        // Reconcile dreamtime jobs for all agents
        foreach (var (name, agentDef) in agents)
        {
            await ReconcileDreamtimeAsync(name, agentDef, logger);
        }

        // Create MobileTransport
        _agentStateCache = new AgentStateCache();
        _mobileSessionStore = new MobileSessionStore(_achatesHome);
        await _mobileSessionStore.MigrateAsync(agents.Keys, cancellationToken);
        _mobileTransport = new MobileTransport(agents, _mobileSessionStore, _agentStateCache, loggerFactory);
        _mobileTransport.AgentReloadFunc = ReloadAgentAsync;
        _mobileTransport.AgentRenameFunc = RenameAgentAsync;
        _mobileTransport.ModelsListFunc = ct => GetAllModelsAsync(ct: ct);
        _mobileTransport.GenerateAvatarFunc = GenerateAvatarAsync;

        // Resolve title model for auto-titling sessions
        var titleModelId = config.Tools?.Title?.Model;
        if (titleModelId is not null)
        {
            try
            {
                _mobileTransport.TitleModel = await ResolveModelAsync(config.Provider?.Name, titleModelId, cancellationToken);
                logger.LogInformation("Title model resolved: {Model}", _mobileTransport.TitleModel.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve title model '{Model}', falling back to agent model", titleModelId);
            }
        }

        _deviceBridge.SetTransport(_mobileTransport);

        // Start cron service for agents that have scheduled tasks enabled
        var cronAgents = agents
            .Where(a => a.Value.CronStore is not null)
            .ToDictionary(a => a.Key, a => (a.Value.CronStore!, a.Value));
        if (cronAgents.Count > 0)
        {
            var reaper = new CronSessionReaper(
                _mobileSessionStore,
                config.Cron,
                loggerFactory.CreateLogger<CronSessionReaper>());
            _cronService = new CronService(cronAgents, _mobileTransport, _mobileSessionStore,
                loggerFactory.CreateLogger<CronService>(), reaper);
            _mobileTransport.CronService = _cronService;
            await _cronService.StartAsync(cancellationToken);
        }

        logger.LogInformation("Started with {AgentCount} agent(s)", agents.Count);
    }

    public async Task<IReadOnlyList<Achates.Providers.Models.Model>> GetAllModelsAsync(
        Achates.Providers.Models.ModelModalities? outputModalities = null,
        CancellationToken ct = default)
    {
        var providerId = config.Provider?.Name
            ?? throw new InvalidOperationException("No provider specified.");

        var provider = ModelProviders.Create(providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

        var apiKey = config.Provider?.ApiKey
            ?? Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException("API key not found.");

        provider.Key = apiKey;
        provider.HttpClient = httpClientFactory.CreateClient("achates");

        return await provider.GetModelsAsync(outputModalities, ct);
    }

    public async Task<byte[]?> GenerateAvatarAsync(string prompt, byte[]? referenceImage, CancellationToken ct)
    {
        var modelId = config.Tools?.Avatar?.Model ?? "google/gemini-2.5-flash-image";
        var images = referenceImage is not null ? new[] { referenceImage } : null;
        return await GenerateImageAsync(modelId, prompt, images, ct);
    }

    public async Task<byte[]?> GenerateImageAsync(string modelId, string prompt,
        IReadOnlyList<byte[]>? referenceImages, CancellationToken ct)
    {
        var providerId = config.Provider?.Name
            ?? throw new InvalidOperationException("No provider specified.");

        var provider = ModelProviders.Create(providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

        var apiKey = config.Provider?.ApiKey
            ?? Environment.GetEnvironmentVariable(provider.EnvironmentKey)
            ?? throw new InvalidOperationException("API key not found.");

        provider.Key = apiKey;
        provider.HttpClient = httpClientFactory.CreateClient("achates");

        return await provider.GenerateImageAsync(modelId, prompt, referenceImages, ct);
    }

    public async Task RenameAgentAsync(string oldName, string newName, string displayName, CancellationToken ct)
    {
        var achatesHome = _achatesHome!;
        var agentsDir = Path.Combine(achatesHome, "agents");
        var oldDir = Path.Combine(agentsDir, oldName);
        var newDir = Path.Combine(agentsDir, newName);

        // 1. Abort active runtimes before touching disk
        _mobileTransport?.EvictRuntimes(oldName);

        // 2. Rename directory (skip if just a display name change)
        if (oldName != newName)
            Directory.Move(oldDir, newDir);

        // 3. Update H1 title in AGENT.md
        var agentFile = Path.Combine(newDir, "AGENT.md");
        var content = await File.ReadAllTextAsync(agentFile, ct);
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("# "))
            {
                lines[i] = $"# {displayName}";
                break;
            }
        }
        await File.WriteAllTextAsync(agentFile, string.Join('\n', lines), ct);

        // 4. Update allowed_chats in other agents
        var updatedAgents = new List<string>();
        if (oldName != newName)
        {
            foreach (var dir in Directory.GetDirectories(agentsDir))
            {
                var name = Path.GetFileName(dir);
                if (name == newName) continue;

                var otherFile = Path.Combine(dir, "AGENT.md");
                if (!File.Exists(otherFile)) continue;

                var otherContent = await File.ReadAllTextAsync(otherFile, ct);
                var otherConfig = AgentLoader.Parse(otherContent);
                if (otherConfig?.AllowChat is null || !otherConfig.AllowChat.Contains(oldName))
                    continue;

                otherConfig.AllowChat = otherConfig.AllowChat
                    .Select(c => c == oldName ? newName : c)
                    .ToList();

                // Preserve display name from H1
                var otherLines = otherContent.Split('\n');
                var otherDisplayName = otherLines.FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim()
                    ?? char.ToUpper(name[0]) + name[1..];

                var markdown = AgentLoader.Serialize(otherDisplayName, otherConfig);
                await File.WriteAllTextAsync(otherFile, markdown, ct);
                updatedAgents.Add(name);
            }
        }

        // 5. Re-key in-memory state
        var newDef = await ResolveAgentAsync(newName,
            AgentLoader.Parse(await File.ReadAllTextAsync(agentFile, ct))!, ct);

        if (oldName != newName)
        {
            _agents.Remove(oldName);
        }
        _agents[newName] = newDef;

        _mobileTransport?.RenameAgent(oldName, newName, newDef);

        if (_cronService is not null && newDef.CronStore is not null)
            _cronService.RenameAgent(oldName, newName, newDef);

        // 6. Reload any agents whose allowed_chats changed
        foreach (var name in updatedAgents)
        {
            try { await ReloadAgentAsync(name, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to reload agent '{Name}' after rename", name);
            }
        }
    }

    public async Task<AgentDefinition> ReloadAgentAsync(string name, CancellationToken ct)
    {
        var agentFile = Path.Combine(_achatesHome!, "agents", name, "AGENT.md");
        var content = await File.ReadAllTextAsync(agentFile, ct);
        var agentConfig = AgentLoader.Parse(content)
            ?? throw new InvalidOperationException($"Failed to parse AGENT.md for agent '{name}'.");

        var agentDef = await ResolveAgentAsync(name, agentConfig, ct);
        _agents[name] = agentDef;
        _mobileTransport?.UpdateAgent(name, agentDef);
        await ReconcileDreamtimeAsync(name, agentDef, logger);
        _cronService?.Poke();
        logger.LogInformation("Agent '{Name}' reloaded with model {Model}", name, agentDef.Model.Id);
        return agentDef;
    }

    private static async Task ReconcileDreamtimeAsync(
        string agentName, AgentDefinition agentDef, ILogger logger)
    {
        if (agentDef.CronStore is null)
            return;

        var jobs = await agentDef.CronStore.LoadAsync();
        var existingJob = jobs.FirstOrDefault(j => j.Kind == CronJobKind.Dreamtime);

        if (agentDef.Dreamtime is { } dreamtime)
        {
            // Build the cron expression from the local time
            var cronExpr = $"{dreamtime.Minute} {dreamtime.Hour} * * *";
            var schedule = new CronSchedule.Cron(cronExpr);

            if (existingJob is null)
            {
                // Create new dreamtime job
                var job = new CronJob
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = "Dreamtime",
                    AgentName = agentName,
                    Schedule = schedule,
                    Message = "Perform your nightly memory review.",
                    Delivery = new CronDeliveryTarget(),
                    Kind = CronJobKind.Dreamtime,
                    State = { NextRunAt = CronScheduler.ComputeNextRun(schedule, DateTimeOffset.UtcNow) },
                };
                await agentDef.CronStore.AddAsync(job);
                logger.LogInformation("Agent '{Name}': created dreamtime job at {Time}", agentName, dreamtime);
            }
            else
            {
                // Update schedule if it changed
                var currentExpr = (existingJob.Schedule as CronSchedule.Cron)?.Expression;
                if (currentExpr != cronExpr)
                {
                    await agentDef.CronStore.UpdateAsync(existingJob.Id, j =>
                    {
                        j.Schedule = schedule;
                        j.State.NextRunAt = CronScheduler.ComputeNextRun(schedule, DateTimeOffset.UtcNow);
                    });
                    logger.LogInformation("Agent '{Name}': updated dreamtime schedule to {Time}", agentName, dreamtime);
                }
            }
        }
        else if (existingJob is not null)
        {
            // Dreamtime was disabled — remove the job
            await agentDef.CronStore.RemoveAsync(existingJob.Id);
            logger.LogInformation("Agent '{Name}': removed dreamtime job", agentName);
        }
    }

    private async Task<AgentDefinition> ResolveAgentAsync(string name, AgentConfig agentConfig, CancellationToken ct)
    {
        var achatesHome = _achatesHome!;
        var model = await ResolveModelAsync(
            agentConfig.Provider ?? config.Provider?.Name,
            agentConfig.Model,
            ct);

        var prompt = ResolvePrompt(agentConfig);

        var toolsConfig = config.Tools;
        var graphClients = CreateGraphClients(toolsConfig?.Graph);
        var withingsClient = CreateWithingsClient(toolsConfig?.Withings);
        if (withingsClient is not null)
            _withingsClient = withingsClient;

        // Resolve thinking model if agent has think tool + thinking model configured
        Model? thinkingModel = null;
        if (agentConfig.Tools?.Contains("think") == true && agentConfig.ThinkingModel is { } thinkingModelId)
        {
            thinkingModel = await ResolveModelAsync(
                agentConfig.Provider ?? config.Provider?.Name,
                thinkingModelId,
                ct);
            logger.LogInformation("Thinking model resolved: {Model}", thinkingModel.Id);
        }

        // Resolve transcription model if any agent uses the transcribe tool
        Model? transcribeModel = null;
        if (agentConfig.Tools?.Contains("transcribe") == true)
        {
            var transcribeModelId = toolsConfig?.Transcribe?.Model ?? "google/gemini-2.5-flash";
            transcribeModel = await ResolveModelAsync(
                agentConfig.Provider ?? config.Provider?.Name,
                transcribeModelId,
                ct);
            logger.LogInformation("Transcribe model resolved: {Model}", transcribeModel.Id);
        }

        var agentDir = Path.Combine(achatesHome, "agents", name);
        var tools = ResolveTools(agentConfig, toolsConfig, model, graphClients, withingsClient,
            name, agentDir, transcribeModel, thinkingModel);
        var hasTools = agentConfig.Tools ?? [];
        var graphAccountNames = graphClients.Keys.ToList();
        var systemPrompt = SystemPrompt.Build(agentConfig.Description, prompt, tools,
            hasNotebook: tools.Any(t => t.Name == "notebook"),
            hasNotes: hasTools.Contains("notes"),
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
            chatAgentNames: agentConfig.AllowChat,
            hasThink: hasTools.Contains("think"));
        var memoryPath = Path.Combine(achatesHome, "agents", name, "memory.md");
        var costLedgerPath = Path.Combine(achatesHome, "agents", name, "costs.jsonl");
        var costLedger = new CostLedger(costLedgerPath);
        var cronStorePath = Path.Combine(achatesHome, "agents", name, "cron.json");
        var cronStore = hasTools.Contains("cron") || agentConfig.Dreamtime is not null
            ? new CronStore(cronStorePath)
            : null;

        var avatarPath = Path.Combine(agentDir, "avatar.jpg");
        if (!File.Exists(avatarPath))
            avatarPath = Path.Combine(agentDir, "avatar.png");
        var avatarData = File.Exists(avatarPath) ? await File.ReadAllBytesAsync(avatarPath, ct) : null;

        var agentDef = new AgentDefinition
        {
            Model = model,
            ThinkingModel = thinkingModel,
            SystemPrompt = systemPrompt,
            Tools = tools,
            CompletionOptions = BuildCompletionOptions(agentConfig.Completion, model),
            MemoryPath = memoryPath,
            DisplayName = agentConfig.Title,
            Description = agentConfig.Description,
            CostLedger = costLedger,
            CronStore = cronStore,
            GraphClients = graphClients,
            AvatarData = avatarData,
            Dreamtime = agentConfig.Dreamtime,
        };

        logger.LogInformation("Agent '{Name}' resolved with model {Model}", name, model.Id);
        return agentDef;
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
        string agentName, string agentDir, Model? transcribeModel = null, Model? thinkingModel = null)
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
                case "cost":
                case "cron":
                case "chat":
                    // Per-session tools — added in MobileTransport.CreateRuntime
                    break;
                case "mail":
                    if (graphClients.Count == 0)
                    { logger.LogWarning("Agent '{Agent}': mail tool skipped — no graph configuration", agentName); break; }
                    tools.Add(new MailTool(graphClients));
                    break;
                case "notes":
                    tools.Add(new NotesTool());
                    break;
                case "notebook":
                    var notebookRoot = ExpandHome(toolsConfig?.Notebook?.Root);
                    if (notebookRoot is null || !Directory.Exists(notebookRoot))
                    { logger.LogWarning("Agent '{Agent}': notebook tool skipped — tools.notebook.root not set or not a directory", agentName); break; }
                    tools.Add(new NotebookTool(notebookRoot));
                    break;
                case "calendar":
                    if (graphClients.Count == 0)
                    { logger.LogWarning("Agent '{Agent}': calendar tool skipped — no graph configuration", agentName); break; }
                    tools.Add(new CalendarTool(graphClients));
                    break;
                case "web_search":
                    var braveKey = toolsConfig?.WebSearch?.BraveApiKey
                        ?? Environment.GetEnvironmentVariable("BRAVE_API_KEY");
                    if (braveKey is null)
                    { logger.LogWarning("Agent '{Agent}': web_search tool skipped — no brave_api_key in config or BRAVE_API_KEY env var", agentName); break; }
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
                    { logger.LogWarning("Agent '{Agent}': health tool skipped — no withings configuration", agentName); break; }
                    tools.Add(new HealthTool(withingsClient));
                    break;
                case "transcribe":
                    if (transcribeModel is null)
                    { logger.LogWarning("Agent '{Agent}': transcribe tool skipped — no transcribe model configured", agentName); break; }
                    tools.Add(new TranscribeTool(transcribeModel));
                    break;
                case "think":
                    if (thinkingModel is null)
                    { logger.LogWarning("Agent '{Agent}': think tool skipped — no thinking model configured", agentName); break; }
                    tools.Add(new ThinkTool(thinkingModel));
                    break;
                case "location":
                    tools.Add(new LocationTool(_deviceBridge));
                    break;
                case "camera":
                    tools.Add(new CameraTool(_deviceBridge));
                    break;
                case "image":
                    var imageModelId = toolsConfig?.Image?.Model;
                    if (string.IsNullOrWhiteSpace(imageModelId))
                    { logger.LogWarning("Agent '{Agent}': image tool skipped — no image model configured (set tools.image.model)", agentName); break; }
                    tools.Add(new ImageTool(agentName, agentDir, imageModelId, GenerateImageAsync));
                    break;
                case "profile":
                    tools.Add(new ProfileTool(agentDir, ct => ReloadAgentAsync(agentName, ct)));
                    break;
                case "agent_creator":
                    tools.Add(new AgentCreatorTool(
                        Path.GetDirectoryName(agentDir)!,
                        model.Id,
                        async (name, ct) => await ReloadAgentAsync(name, ct)));
                    break;
                default:
                    logger.LogWarning("Agent '{Agent}': unknown tool '{Tool}' — skipped. Remove it from AGENT.md or check the spelling.", agentName, toolName);
                    break;
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

        var models = await provider.GetModelsAsync(cancellationToken: cancellationToken);
        return models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' not found.");
    }
}
