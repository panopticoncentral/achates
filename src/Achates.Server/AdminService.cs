using Achates.Agent.Messages;
using Achates.Configuration;
using Achates.Server.Cron;

namespace Achates.Server;

public sealed class AdminService(GatewayService gatewayService, AchatesConfig config)
{
    private static readonly string AchatesHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates");

    // --- Dashboard ---

    public IReadOnlyList<ChannelBinding> GetBindings() =>
        gatewayService.Gateway.Bindings;

    public ICollection<string> GetActiveSessionKeys() =>
        gatewayService.Gateway.ActiveSessionKeys;

    // --- Sessions ---

    public List<SessionInfo> GetSessionFiles()
    {
        var sessionsPath = Path.Combine(AchatesHome, "sessions");
        if (!Directory.Exists(sessionsPath))
            return [];

        var sessions = new List<SessionInfo>();
        foreach (var channelDir in Directory.GetDirectories(sessionsPath))
        {
            var channelName = Path.GetFileName(channelDir);
            foreach (var file in Directory.GetFiles(channelDir, "*.json"))
            {
                var fi = new FileInfo(file);
                sessions.Add(new SessionInfo
                {
                    Channel = channelName,
                    Peer = Path.GetFileNameWithoutExtension(file),
                    SizeBytes = fi.Length,
                    LastModified = fi.LastWriteTimeUtc,
                });
            }
        }

        return sessions;
    }

    public async Task<IReadOnlyList<AgentMessage>?> LoadSessionAsync(string channel, string peer)
    {
        var key = $"{channel}:{peer}";
        return await gatewayService.SessionStore.LoadAsync(key);
    }

    public async Task DeleteSessionAsync(string channel, string peer)
    {
        var key = $"{channel}:{peer}";
        await gatewayService.Gateway.RemoveSessionAsync(key);
    }

    // --- Memory ---

    public List<MemoryInfo> GetMemoryFiles()
    {
        var agentsPath = Path.Combine(AchatesHome, "agents");
        if (!Directory.Exists(agentsPath))
            return [];

        var memories = new List<MemoryInfo>();
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

        return memories;
    }

    public async Task<string> LoadMemoryAsync(string agentName)
    {
        var path = Path.Combine(AchatesHome, "agents", agentName, "memory.md");
        if (!File.Exists(path))
            return "";
        return await File.ReadAllTextAsync(path);
    }

    public async Task SaveMemoryAsync(string agentName, string content)
    {
        var path = Path.Combine(AchatesHome, "agents", agentName, "memory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    // --- Costs ---

    public CostLedger? GetCostLedger(string agentName)
    {
        var bindings = gatewayService.Gateway.Bindings;
        return bindings.FirstOrDefault(b => b.AgentName == agentName)?.Agent.CostLedger;
    }

    public IEnumerable<string> GetAgentNames() =>
        gatewayService.Gateway.Bindings
            .Select(b => b.AgentName)
            .Distinct();

    // --- Jobs ---

    public async Task<List<(string AgentName, CronJob Job)>> GetAllJobsAsync()
    {
        var result = new List<(string, CronJob)>();
        foreach (var binding in gatewayService.Gateway.Bindings)
        {
            if (binding.Agent.CronStore is not { } store)
                continue;

            // Avoid duplicates if multiple channels share the same agent
            if (result.Any(r => r.Item1 == binding.AgentName))
                continue;

            var jobs = await store.LoadAsync();
            foreach (var job in jobs)
                result.Add((binding.AgentName, job));
        }
        return result;
    }

    public CronStore? GetCronStore(string agentName)
    {
        return gatewayService.Gateway.Bindings
            .FirstOrDefault(b => b.AgentName == agentName)?.Agent.CronStore;
    }

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
        // Mask values for known sensitive keys
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("token:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("client_secret:", StringComparison.OrdinalIgnoreCase)
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
    public required string Channel { get; init; }
    public required string Peer { get; init; }
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
