using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Achates.Configuration;

public static class ConfigLoader
{
    public static readonly string DefaultConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates");

    public static readonly string DefaultConfigPath =
        Path.Combine(DefaultConfigDir, "config.yaml");

    /// <summary>
    /// Load config from ACHATES_CONFIG_PATH env var, or ~/.achates/config.yaml.
    /// Creates a default config file if one does not exist.
    /// </summary>
    public static AchatesConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH")
                   ?? DefaultConfigPath;

        if (!File.Exists(path))
        {
            var config = CreateDefault();
            Save(config, path);
            return config;
        }

        var yaml = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<AchatesConfig>(yaml) ?? new AchatesConfig();
    }

    public static void Save(AchatesConfig config, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH")
                 ?? DefaultConfigPath;

        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        File.WriteAllText(path, serializer.Serialize(config));
    }

    private static AchatesConfig CreateDefault() => new()
    {
        Provider = new ProviderConfig { Name = "openrouter" },
        Agents = new Dictionary<string, AgentConfig>
        {
            ["default"] = new()
            {
                Model = "anthropic/claude-sonnet-4",
                Tools = ["session", "memory"],
                Completion = new CompletionConfig
                {
                    ReasoningEffort = "medium",
                },
                Channels = new Dictionary<string, ChannelConfig>
                {
                    ["websocket"] = new(),
                },
            },
        },
        Console = new ConsoleConfig
        {
            Url = "ws://localhost:5000/ws",
        },
    };
}
