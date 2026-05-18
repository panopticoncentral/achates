using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;
using Achates.Server.Cron;
using Achates.Server.Mobile;
using Achates.Server.Tools;

namespace Achates.Tests;

public sealed class SessionsToolTests : IDisposable
{
    private const string Agent = "agent1";
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"achates-test-{Guid.NewGuid():N}");
    private readonly MobileSessionStore _store;

    public SessionsToolTests() => _store = new MobileSessionStore(_basePath);

    public void Dispose()
    {
        if (Directory.Exists(_basePath)) Directory.Delete(_basePath, true);
    }

    private SessionsTool Tool(string? currentSessionId = null, DateTimeOffset? since = null) =>
        new(_store, Agent, currentSessionId, since);

    private static async Task<string> Run(SessionsTool tool, Dictionary<string, object?> args)
    {
        var result = await tool.ExecuteAsync("call-1", args);
        return ((CompletionTextContent)result.Content[0]).Text;
    }

    private async Task Save(MobileSession s) => await _store.SaveAsync(Agent, s);

    [Fact]
    public async Task List_ExcludesCurrentSession()
    {
        await Save(new MobileSession { Id = "cur", Title = "Current", Messages = [new UserMessage { Text = "hi" }] });
        await Save(new MobileSession { Id = "other", Title = "Other", Messages = [new UserMessage { Text = "yo" }] });

        var text = await Run(Tool(currentSessionId: "cur"), new() { ["action"] = "list" });

        Assert.Contains("`other`", text);
        Assert.DoesNotContain("`cur`", text);
    }

    [Fact]
    public async Task Read_RejectsCurrentSession()
    {
        await Save(new MobileSession { Id = "cur", Title = "Current" });

        var text = await Run(Tool(currentSessionId: "cur"),
            new() { ["action"] = "read", ["session_id"] = "cur" });

        Assert.Contains("current session", text);
    }

    [Fact]
    public async Task Read_ReturnsTranscript()
    {
        await Save(new MobileSession
        {
            Id = "s1",
            Title = "Talk",
            Messages =
            [
                new UserMessage { Text = "what is the plan" },
                new SummaryMessage { Summary = "we decided to ship friday" },
            ],
        });

        var text = await Run(Tool(), new() { ["action"] = "read", ["session_id"] = "s1" });

        Assert.Contains("what is the plan", text);
        Assert.Contains("we decided to ship friday", text);
    }

    [Fact]
    public async Task Read_RendersChatOriginSpeech()
    {
        await Save(new MobileSession
        {
            Id = "chat-1",
            Title = "Chat with Val",
            Source = SessionSource.Chat,
            Messages =
            [
                new AgentSpeechMessage
                {
                    SpeakerAgentId = "paul", SpeakerDisplayName = "Paul", ToAgentId = "val",
                    Text = "what did you think of the proposal",
                },
                new AgentSpeechMessage
                {
                    SpeakerAgentId = "val", SpeakerDisplayName = "Val", ToAgentId = "paul",
                    Text = "the budget section needs work but the timeline is solid",
                },
            ],
        });

        var text = await Run(Tool(), new() { ["action"] = "read", ["session_id"] = "chat-1" });

        Assert.Contains("what did you think of the proposal", text);
        Assert.Contains("the budget section needs work but the timeline is solid", text);
        Assert.Contains("Paul", text);
        Assert.Contains("Val", text);
    }

    [Fact]
    public async Task Search_MatchesChatOriginSpeechBody()
    {
        await Save(new MobileSession
        {
            Id = "chat-1",
            Title = "Random",
            Source = SessionSource.Chat,
            Messages =
            [
                new AgentSpeechMessage
                {
                    SpeakerAgentId = "val", SpeakerDisplayName = "Val", ToAgentId = "paul",
                    Text = "we should revisit the quarterly forecast next week",
                },
            ],
        });

        var text = await Run(Tool(), new() { ["action"] = "search", ["query"] = "quarterly forecast" });

        Assert.Contains("`chat-1`", text);
        Assert.Contains("quarterly forecast", text);
    }

    [Fact]
    public async Task Search_Tier1_MatchesTitleWithoutBodyScan()
    {
        await Save(new MobileSession
        {
            Id = "s1",
            Title = "Vacation planning",
            Messages = [new UserMessage { Text = "unrelated text" }],
        });

        var text = await Run(Tool(), new() { ["action"] = "search", ["query"] = "vacation" });

        Assert.Contains("`s1`", text);
        Assert.DoesNotContain("…", text); // metadata match → no body snippet
    }

    [Fact]
    public async Task Search_Tier2_MatchesBodyWithSnippet()
    {
        await Save(new MobileSession
        {
            Id = "s1",
            Title = "Random chat",
            Messages =
            [
                new UserMessage { Text = "hello there" },
                new SummaryMessage { Summary = "discussed the quarterly budget figures at length" },
            ],
        });

        var text = await Run(Tool(), new() { ["action"] = "search", ["query"] = "quarterly budget" });

        Assert.Contains("`s1`", text);
        Assert.Contains("quarterly budget", text);
        Assert.Contains("…", text); // body match → snippet shown
    }

    [Fact]
    public async Task Search_RanksMetadataMatchesBeforeBodyMatches()
    {
        await Save(new MobileSession
        {
            Id = "body",
            Title = "Nothing relevant",
            Messages = [new UserMessage { Text = "x" }, new SummaryMessage { Summary = "mentions widget once" }],
        });
        await Save(new MobileSession
        {
            Id = "meta",
            Title = "Widget design",
            Messages = [new UserMessage { Text = "x" }],
        });

        var text = await Run(Tool(), new() { ["action"] = "search", ["query"] = "widget" });

        Assert.True(text.IndexOf("`meta`", StringComparison.Ordinal)
                  < text.IndexOf("`body`", StringComparison.Ordinal),
            "metadata match should rank before body match");
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsMessage()
    {
        await Save(new MobileSession { Id = "s1", Title = "Cats", Messages = [new UserMessage { Text = "meow" }] });

        var text = await Run(Tool(), new() { ["action"] = "search", ["query"] = "zzzznomatch" });

        Assert.Contains("No sessions matched", text);
    }

    [Fact]
    public async Task List_TagsOrigin_ChatCronDreamtimeUser()
    {
        await Save(new MobileSession
        {
            Id = "chat", Title = "Chat", Source = SessionSource.Chat,
            Messages = [new UserMessage { Text = "hi" }],
        });
        await Save(new MobileSession
        {
            Id = "cron", Title = "Cron", JobId = "job-1",
            Messages = [new UserMessage { Text = "hi" }],
        });
        await Save(new MobileSession
        {
            Id = "dream", Title = "Dream",
            Messages = [new UserMessage { Text = CronSessionMarker.FormatHeader("Dreamtime"), Hidden = true }],
        });
        await Save(new MobileSession
        {
            Id = "user", Title = "User",
            Messages = [new UserMessage { Text = "hi" }],
        });

        var text = await Run(Tool(), new() { ["action"] = "list" });

        Assert.Contains("`chat`) — [chat]", text);
        Assert.Contains("`cron`) — [cron]", text);
        Assert.Contains("`dream`) — [dreamtime]", text);
        Assert.Contains("`user`) — [user]", text);
    }

    [Fact]
    public async Task SinceFilter_ExcludesEverythingWhenInFuture()
    {
        await Save(new MobileSession { Id = "s1", Title = "Old", Messages = [new UserMessage { Text = "hi" }] });

        var future = await Run(Tool(since: DateTimeOffset.UtcNow.AddMinutes(5)),
            new() { ["action"] = "list" });
        Assert.Contains("No sessions since last review", future);

        var past = await Run(Tool(since: DateTimeOffset.UtcNow.AddMinutes(-5)),
            new() { ["action"] = "list" });
        Assert.Contains("`s1`", past);
    }
}
