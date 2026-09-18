using Achates.Server;

namespace Achates.Tests;

[CollectionDefinition("Configuration environment", DisableParallelization = true)]
public sealed class ConfigurationEnvironmentCollection { }

[Collection("Configuration environment")]
public class ConfigLoaderTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable("ACHATES_HOME");
    private readonly string? _originalConfig = Environment.GetEnvironmentVariable("ACHATES_CONFIG_PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ACHATES_HOME", _originalHome);
        Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", _originalConfig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DataDir_UnsetOrBlank_UsesLegacyDirectory(string? home)
    {
        Environment.SetEnvironmentVariable("ACHATES_HOME", home);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".achates"),
            ConfigLoader.DataDir);
    }

    [Theory]
    [InlineData("~/Documents/Achates", "Documents/Achates")]
    [InlineData("~", "")]
    public void DataDir_ExpandsTilde(string home, string suffix)
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), suffix),
            ConfigLoader.ResolveDataDir(home));
    }

    [Fact]
    public void DataDir_RelativePath_IsAbsolute()
    {
        Assert.Equal(Path.GetFullPath("Achates Data"), ConfigLoader.ResolveDataDir("Achates Data"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomHome_ConfigAndAgentPersistence_UseSelectedRoot(bool overrideConfig)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"achates-home-{Guid.NewGuid():N}");
        var home = Path.Combine(temp, "Achates Data");
        var configPath = overrideConfig ? Path.Combine(temp, "separate", "settings.yaml") : Path.Combine(home, "config.yaml");
        try
        {
            Environment.SetEnvironmentVariable("ACHATES_HOME", home);
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", overrideConfig ? configPath : null);
            var config = ConfigLoader.Load();
            config.Models = new ModelsConfig { Base = "test-model" };
            ConfigLoader.Save(config);
            Assert.Equal("test-model", ConfigLoader.Load().Models?.Base);
            Assert.True(File.Exists(configPath));
            Assert.Equal(home, ConfigLoader.DataDir);
            if (overrideConfig)
                Assert.False(File.Exists(Path.Combine(home, "config.yaml")));

            AgentLoader.CreateDefault(ConfigLoader.DataDir);
            Assert.Contains("default", AgentLoader.LoadAgents(ConfigLoader.DataDir).Keys);
            var sessions = new Achates.Server.Mobile.MobileSessionStore(ConfigLoader.DataDir);
            await sessions.SaveAsync("default", new Achates.Server.Mobile.MobileSession { Id = "test", Title = "Relocated" });
            Assert.Single(Directory.GetFiles(Path.Combine(home, "agents", "default", "sessions"), "*.json"));
            Assert.Equal("Relocated", (await sessions.LoadAsync("default", "test"))?.Title);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"achates-cfg-{Guid.NewGuid():N}.yaml");

    [Fact]
    public void Models_RoundTrip_PreservesBaseAndThinking()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = "anthropic/claude-opus-4.7" },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var loaded = ConfigLoader.Load();

            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Equal("anthropic/claude-opus-4.7", loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Models_NullThinking_OmittedAndLoadsAsNull()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = null },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.DoesNotContain("thinking", text);
            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Null(loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Library_Root_RoundTrips()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Tools = new ToolsConfig { Library = new LibraryConfig { Root = "~/library" } },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var loaded = ConfigLoader.Load();

            Assert.Equal("~/library", loaded.Tools?.Library?.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Memory_DefaultBudget_RoundTrips()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Memory = new MemoryConfig { DefaultBudgetTokens = 8000 },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.Contains("default_budget_tokens", text);
            Assert.Equal(8000, loaded.Memory?.DefaultBudgetTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Memory_DefaultWorkingBudget_RoundTrips()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Memory = new MemoryConfig { DefaultWorkingBudgetTokens = 750 },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.Contains("default_working_budget_tokens", text);
            Assert.Equal(750, loaded.Memory?.DefaultWorkingBudgetTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }
}
