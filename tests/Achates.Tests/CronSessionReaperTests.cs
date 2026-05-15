using System.Text.Json;
using Achates.Agent.Messages;
using Achates.Server;
using Achates.Server.Cron;
using Achates.Server.Mobile;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achates.Tests;

/// <summary>
/// Tests for <see cref="CronSessionReaper"/>. The reaper must recognize cron-origin
/// sessions by JobId OR the [Scheduled task: ...] fingerprint (so sessions whose
/// JobId stamp was lost still get pruned), apply keep-N only to User-kind jobs, and
/// bound dreamtime by max-age while keeping its nightly history.
/// </summary>
public class CronSessionReaperTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string Agent = "tester";

    private static string NewTempBase()
    {
        var path = Path.Combine(Path.GetTempPath(), "achates-reaper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(path, "agents", Agent, "sessions"));
        return path;
    }

    /// <summary>Writes a session file directly so we control the Updated timestamp.</summary>
    private static void WriteSession(
        string basePath, string id, DateTimeOffset updated,
        string? jobId, string? firstMessageText, bool hiddenFirst = true)
    {
        var session = new MobileSession
        {
            Id = id,
            Title = id,
            Created = updated,
            Updated = updated,
            JobId = jobId,
            Messages = firstMessageText is null
                ? []
                : [new UserMessage { Text = firstMessageText, Hidden = hiddenFirst }],
        };

        var dir = Path.Combine(basePath, "agents", Agent, "sessions");
        var file = Path.Combine(dir, $"20200101-000000_{id}_s.json");
        File.WriteAllText(file, JsonSerializer.Serialize(session, JsonOptions));
    }

    private static string[] RemainingIds(string basePath)
        => Directory.GetFiles(Path.Combine(basePath, "agents", Agent, "sessions"))
            .Select(MobileSessionStore.ExtractSessionId)
            .OrderBy(x => x)
            .ToArray();

    private static async Task SweepAsync(
        string basePath, CronConfig? config, IReadOnlyList<CronJob> jobs)
    {
        var store = new MobileSessionStore(basePath);
        var reaper = new CronSessionReaper(store, config, NullLogger<CronSessionReaper>.Instance);
        await reaper.SweepAsync([(Agent, jobs)], force: true);
    }

    [Theory]
    [InlineData("Daily Briefing")]
    [InlineData("Dreamtime")]
    [InlineData("Job with: colon")]
    public void Marker_RoundTrips(string name)
    {
        var header = CronSessionMarker.FormatHeader(name);
        Assert.Equal(name, CronSessionMarker.TryParseJobName(header + "\n\nbody"));
    }

    [Fact]
    public void Marker_RejectsNonMarkerText()
    {
        Assert.Null(CronSessionMarker.TryParseJobName("just a normal message"));
        Assert.Null(CronSessionMarker.TryParseJobName(null));
        Assert.Null(CronSessionMarker.TryParseJobName("[Scheduled task: ]\n\nx"));
    }

    [Fact]
    public async Task OrphanUserSessions_PrunedByFingerprint_KeepsNewest()
    {
        var basePath = NewTempBase();
        var now = DateTimeOffset.UtcNow;
        var marker = CronSessionMarker.FormatHeader("Daily Briefing") + "\n\nbody";

        // All cron-origin via fingerprint, JobId lost (null). Recent so max-age is moot.
        WriteSession(basePath, "old1", now.AddDays(-3), jobId: null, marker);
        WriteSession(basePath, "old2", now.AddDays(-2), jobId: null, marker);
        WriteSession(basePath, "new1", now.AddDays(-1), jobId: null, marker);

        await SweepAsync(basePath, new CronConfig { KeepLastPerJob = 1, MaxAgeDays = 365 }, []);

        Assert.Equal(["new1"], RemainingIds(basePath));
    }

    [Fact]
    public async Task StampedUserSessions_PrunedByJobId()
    {
        var basePath = NewTempBase();
        var now = DateTimeOffset.UtcNow;
        var job = new CronJob
        {
            Id = "job1",
            Name = "Daily Briefing",
            AgentName = Agent,
            Schedule = new CronSchedule.Every(TimeSpan.FromMinutes(5)),
            Message = "x",
            Delivery = new CronDeliveryTarget(),
        };

        WriteSession(basePath, "run1", now.AddDays(-2), jobId: "job1", firstMessageText: "anything");
        WriteSession(basePath, "run2", now.AddDays(-1), jobId: "job1", firstMessageText: "anything");

        await SweepAsync(basePath, new CronConfig { KeepLastPerJob = 1, MaxAgeDays = 365 }, [job]);

        Assert.Equal(["run2"], RemainingIds(basePath));
    }

    [Fact]
    public async Task Dreamtime_NoKeepN_KeepsRecentHistory_ButMaxAgePrunes()
    {
        var basePath = NewTempBase();
        var now = DateTimeOffset.UtcNow;
        var dream = CronSessionMarker.FormatHeader("Dreamtime") + "\n\nreview";

        // Five recent nightly dreamtime sessions — all kept (no keep-N for dreamtime).
        for (var i = 1; i <= 5; i++)
            WriteSession(basePath, $"d{i}", now.AddDays(-i), jobId: null, dream);

        // One ancient dreamtime session — pruned by max-age only.
        WriteSession(basePath, "dancient", now.AddDays(-40), jobId: null, dream);

        await SweepAsync(basePath, new CronConfig { KeepLastPerJob = 1, MaxAgeDays = 30 }, []);

        Assert.Equal(["d1", "d2", "d3", "d4", "d5"], RemainingIds(basePath));
    }

    [Fact]
    public async Task NonCronSessions_NeverDeleted()
    {
        var basePath = NewTempBase();
        var now = DateTimeOffset.UtcNow;

        // Old, no JobId, no fingerprint, and a non-hidden first message — a real
        // user conversation. Must survive even though it is well past max-age.
        WriteSession(basePath, "chat1", now.AddDays(-400), jobId: null,
            firstMessageText: "hey what's up", hiddenFirst: false);

        await SweepAsync(basePath, new CronConfig { KeepLastPerJob = 1, MaxAgeDays = 30 }, []);

        Assert.Equal(["chat1"], RemainingIds(basePath));
    }
}
