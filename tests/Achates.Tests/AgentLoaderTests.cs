using Achates.Server;

namespace Achates.Tests;

public sealed class AgentLoaderTests
{
    [Theory]
    [InlineData("Paul", "paul")]
    [InlineData("Paul's Assistant", "pauls-assistant")]
    [InlineData("My Agent 2", "my-agent-2")]
    [InlineData("Valerie", "valerie")]
    [InlineData("  Spaces  ", "spaces")]
    [InlineData("--dashes--", "dashes")]
    [InlineData("a--b", "a-b")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("hello world!", "hello-world")]
    [InlineData("René", "ren")]
    [InlineData("", null)]
    [InlineData("!!!", null)]
    public void NormalizeId_ProducesExpectedResult(string input, string? expected)
    {
        var result = AgentLoader.NormalizeId(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeId_TruncatesAt64Characters()
    {
        var longName = new string('a', 100);
        var result = AgentLoader.NormalizeId(longName);
        Assert.NotNull(result);
        Assert.Equal(64, result!.Length);
    }

    [Theory]
    [InlineData("**Shared Memory:** false", false)]
    [InlineData("**Shared Memory:** False", false)]
    [InlineData("**Shared Memory:** true", true)]
    [InlineData("**Shared Memory:** TRUE", true)]
    public void Parse_ReadsSharedMemoryCapability(string capabilityLine, bool expected)
    {
        var md = $"""
            # Test

            ## Capabilities

            {capabilityLine}
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Equal(expected, config!.SharedMemory);
    }

    [Fact]
    public void Parse_LeavesSharedMemoryNullWhenAbsent()
    {
        var md = """
            # Test

            ## Capabilities

            **Tools:**
              - memory
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Null(config!.SharedMemory);
    }

    [Fact]
    public void Parse_LeavesSharedMemoryNullOnInvalidValue()
    {
        var md = """
            # Test

            ## Capabilities

            **Shared Memory:** maybe
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Null(config!.SharedMemory);
    }

    [Fact]
    public void Serialize_EmitsSharedMemory_WhenFalse()
    {
        var config = new AgentConfig { SharedMemory = false };
        Assert.Contains("**Shared Memory:** false", AgentLoader.Serialize("Test", config));
    }

    [Fact]
    public void Serialize_OmitsSharedMemory_WhenTrue()
    {
        var config = new AgentConfig { SharedMemory = true };
        Assert.DoesNotContain("Shared Memory", AgentLoader.Serialize("Test", config));
    }

    [Fact]
    public void Serialize_OmitsSharedMemory_WhenNull()
    {
        var config = new AgentConfig { SharedMemory = null };
        Assert.DoesNotContain("Shared Memory", AgentLoader.Serialize("Test", config));
    }

    [Fact]
    public void Parse_Serialize_Roundtrips_SharedMemoryFalse()
    {
        var original = new AgentConfig
        {
            Title = "Test",
            Description = "desc",
            SharedMemory = false,
            Prompt = "prompt",
        };

        var md = AgentLoader.Serialize("Test", original);
        var roundtripped = AgentLoader.Parse(md);

        Assert.NotNull(roundtripped);
        Assert.False(roundtripped!.SharedMemory);
    }

    [Fact]
    public void Parse_reads_voice_capability()
    {
        var md = """
            # Test Agent

            ## Capabilities

            **Voice:** af_nicole

            ## Prompt

            hello
            """;

        var config = AgentLoader.Parse(md);

        Assert.NotNull(config);
        Assert.Equal("af_nicole", config!.Voice);
    }

    [Fact]
    public void Parse_reads_voice_blend()
    {
        var md = """
            # Test

            ## Capabilities

            **Voice:** af_nicole(0.7)+af_bella(0.3)
            """;

        var config = AgentLoader.Parse(md);

        Assert.Equal("af_nicole(0.7)+af_bella(0.3)", config!.Voice);
    }

    [Fact]
    public void Serialize_emits_voice_capability()
    {
        var config = new AgentConfig
        {
            Title = "Test",
            Description = "",
            Voice = "af_nicole",
            Prompt = "hi",
        };

        var md = AgentLoader.Serialize("test", config);

        Assert.Contains("**Voice:** af_nicole", md);
    }

    [Fact]
    public void Parse_voice_capability_absent_yields_null()
    {
        var md = """
            # Test

            ## Capabilities

            **Model:** anthropic/claude-sonnet-4.6

            ## Prompt

            hi
            """;

        var config = AgentLoader.Parse(md);
        Assert.Null(config!.Voice);
    }

    [Fact]
    public void RenameAgent_OnDisk_MovesDirectoryAndUpdatesCrossReferences()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"achates-rename-test-{Guid.NewGuid():N}");
        try
        {
            // Setup: create two agent directories
            var paulDir = Path.Combine(basePath, "agents", "paul");
            var valDir = Path.Combine(basePath, "agents", "val");
            Directory.CreateDirectory(paulDir);
            Directory.CreateDirectory(valDir);

            File.WriteAllText(Path.Combine(paulDir, "AGENT.md"), """
                # Paul

                Personal assistant.

                ## Capabilities

                **Model:** anthropic/claude-sonnet-4
                """.Replace("                ", ""));

            File.WriteAllText(Path.Combine(valDir, "AGENT.md"), """
                # Val

                Another assistant.

                ## Capabilities

                **Model:** anthropic/claude-sonnet-4

                **Allowed Chats:**
                  - paul
                """.Replace("                ", ""));

            // Act: rename paul -> pablo
            var newDir = Path.Combine(basePath, "agents", "pablo");
            Directory.Move(paulDir, newDir);

            // Update AGENT.md title
            var agentFile = Path.Combine(newDir, "AGENT.md");
            var content = File.ReadAllText(agentFile);
            content = content.Replace("# Paul", "# Pablo");
            File.WriteAllText(agentFile, content);

            // Update val's allowed_chats
            var valFile = Path.Combine(valDir, "AGENT.md");
            var valConfig = AgentLoader.Parse(File.ReadAllText(valFile))!;
            Assert.Contains("paul", valConfig.AllowChat!);
            valConfig.AllowChat = valConfig.AllowChat!.Select(c => c == "paul" ? "pablo" : c).ToList();
            File.WriteAllText(valFile, AgentLoader.Serialize("Val", valConfig));

            // Assert
            Assert.False(Directory.Exists(paulDir));
            Assert.True(Directory.Exists(newDir));
            Assert.Contains("# Pablo", File.ReadAllText(agentFile));

            var updatedValConfig = AgentLoader.Parse(File.ReadAllText(valFile))!;
            Assert.Contains("pablo", updatedValConfig.AllowChat!);
            Assert.DoesNotContain("paul", updatedValConfig.AllowChat!);
        }
        finally
        {
            if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
        }
    }
}
