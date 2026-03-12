using System.Text.Json;
using System.Text.Json.Serialization;

namespace Achates.Server;

public sealed record CostEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Model { get; init; }
    public required string Channel { get; init; }
    public required string Peer { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }
    public decimal CostTotal { get; init; }
    public decimal CostInput { get; init; }
    public decimal CostOutput { get; init; }
    public decimal CostCacheRead { get; init; }
    public decimal CostCacheWrite { get; init; }
}

/// <summary>
/// Append-only JSONL ledger for recording per-completion costs.
/// Thread-safe via semaphore (multiple peers may share one agent's ledger).
/// </summary>
public sealed class CostLedger(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task AppendAsync(CostEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(filePath, line + "\n").ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CostEntry>> QueryAsync(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        if (!File.Exists(filePath))
            return [];

        await _lock.WaitAsync().ConfigureAwait(false);
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(filePath).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        var entries = new List<CostEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<CostEntry>(line, JsonOptions);
                if (entry is null)
                    continue;

                if (from is not null && entry.Timestamp < from.Value)
                    continue;
                if (to is not null && entry.Timestamp > to.Value)
                    continue;

                entries.Add(entry);
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
        }

        return entries;
    }
}
