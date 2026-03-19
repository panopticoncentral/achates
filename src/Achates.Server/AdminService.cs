using Achates.Server.Cron;
using Achates.Server.Mobile;

namespace Achates.Server;

public sealed class AdminService(GatewayService gatewayService, AchatesConfig config)
{
    private static readonly string AchatesHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates");

    // --- Agents ---

    public IReadOnlyDictionary<string, AgentDefinition> GetAgents() =>
        gatewayService.Agents;

    public IEnumerable<string> GetAgentNames() =>
        gatewayService.Agents.Keys;

    // --- Sessions ---

    public List<SessionInfo> GetSessionFiles()
    {
        var agentsPath = Path.Combine(AchatesHome, "agents");
        if (!Directory.Exists(agentsPath))
            return [];

        var sessions = new List<SessionInfo>();
        foreach (var agentDir in Directory.GetDirectories(agentsPath))
        {
            var agentName = Path.GetFileName(agentDir);
            var sessionsDir = Path.Combine(agentDir, "sessions");
            if (!Directory.Exists(sessionsDir))
                continue;

            foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
            {
                var fi = new FileInfo(file);
                sessions.Add(new SessionInfo
                {
                    AgentName = agentName,
                    SessionId = Path.GetFileNameWithoutExtension(file),
                    SizeBytes = fi.Length,
                    LastModified = fi.LastWriteTimeUtc,
                });
            }
        }

        return sessions;
    }

    public async Task<MobileSession?> LoadSessionAsync(string agentName, string sessionId)
    {
        var store = gatewayService.MobileSessionStore;
        if (store is null) return null;
        return await store.LoadAsync(agentName, sessionId);
    }

    public async Task DeleteSessionAsync(string agentName, string sessionId)
    {
        var store = gatewayService.MobileSessionStore;
        if (store is null) return;
        await store.DeleteAsync(agentName, sessionId);
    }

    // --- Memory ---

    public List<MemoryInfo> GetMemoryFiles()
    {
        var memories = new List<MemoryInfo>();

        var sharedPath = Path.Combine(AchatesHome, "memory.md");
        if (File.Exists(sharedPath))
        {
            var fi = new FileInfo(sharedPath);
            memories.Add(new MemoryInfo
            {
                AgentName = "(shared)",
                FilePath = sharedPath,
                SizeBytes = fi.Length,
                LastModified = fi.LastWriteTimeUtc,
            });
        }

        var agentsPath = Path.Combine(AchatesHome, "agents");
        if (Directory.Exists(agentsPath))
        {
            foreach (var agentDir in Directory.GetDirectories(agentsPath))
            {
                var agentName = Path.GetFileName(agentDir);
                var memoryFile = Path.Combine(agentDir, "memory.md");
                if (File.Exists(memoryFile))
                {
                    var fi = new FileInfo(memoryFile);
                    memories.Add(new MemoryInfo
                    {
                        AgentName = agentName,
                        FilePath = memoryFile,
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTimeUtc,
                    });
                }
            }
        }

        return memories;
    }

    public async Task<string> LoadMemoryAsync(string agentName)
    {
        var path = agentName == "(shared)"
            ? Path.Combine(AchatesHome, "memory.md")
            : Path.Combine(AchatesHome, "agents", agentName, "memory.md");
        if (!File.Exists(path))
            return "";
        return await File.ReadAllTextAsync(path);
    }

    public async Task SaveMemoryAsync(string agentName, string content)
    {
        var path = agentName == "(shared)"
            ? Path.Combine(AchatesHome, "memory.md")
            : Path.Combine(AchatesHome, "agents", agentName, "memory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    // --- Costs ---

    public CostLedger? GetCostLedger(string agentName) =>
        gatewayService.Agents.TryGetValue(agentName, out var agent)
            ? agent.CostLedger
            : null;

    // --- Jobs ---

    public async Task<List<(string AgentName, CronJob Job)>> GetAllJobsAsync()
    {
        var result = new List<(string, CronJob)>();
        foreach (var (agentName, agentDef) in gatewayService.Agents)
        {
            if (agentDef.CronStore is not { } store)
                continue;

            var jobs = await store.LoadAsync();
            foreach (var job in jobs)
                result.Add((agentName, job));
        }
        return result;
    }

    public CronStore? GetCronStore(string agentName) =>
        gatewayService.Agents.TryGetValue(agentName, out var agent)
            ? agent.CronStore
            : null;

    // --- Config ---

    public async Task<string> GetRawConfigAsync()
    {
        var path = Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH")
            ?? Path.Combine(AchatesHome, "config.yaml");

        if (!File.Exists(path))
            return "(config file not found)";

        var yaml = await File.ReadAllTextAsync(path);
        return MaskSecrets(yaml);
    }

    public AchatesConfig GetParsedConfig() => config;

    private static string MaskSecrets(string yaml)
    {
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("token:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("client_secret:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("api_key:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("brave_api_key:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("client_id:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx >= 0)
                    lines[i] = line[..(colonIdx + 1)] + " ***";
            }
        }

        return string.Join('\n', lines);
    }
}

public sealed record SessionInfo
{
    public required string AgentName { get; init; }
    public required string SessionId { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastModified { get; init; }
}

public sealed record MemoryInfo
{
    public required string AgentName { get; init; }
    public required string FilePath { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastModified { get; init; }
}
