using System.Text.Json;

namespace Achates.Server.Cron;

/// <summary>
/// File-backed store for cron jobs. Thread-safe via semaphore.
/// Stores at ~/.achates/agents/{agentName}/cron.json.
/// </summary>
public sealed class CronStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<CronJob>? _cache;

    public async Task<IReadOnlyList<CronJob>> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null)
                return _cache;

            if (!File.Exists(filePath))
            {
                _cache = [];
                return _cache;
            }

            await using var stream = File.OpenRead(filePath);
            var doc = await JsonSerializer.DeserializeAsync<CronStoreDocument>(stream, JsonOptions, ct);
            _cache = doc?.Jobs ?? [];
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CronJob> AddAsync(CronJob job, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureCacheAsync(ct);
            _cache!.Add(job);
            await PersistAsync(ct);
            return job;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CronJob?> UpdateAsync(string jobId, Action<CronJob> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureCacheAsync(ct);
            var job = _cache!.Find(j => j.Id == jobId);
            if (job is null) return null;

            mutate(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistAsync(ct);
            return job;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureCacheAsync(ct);
            var removed = _cache!.RemoveAll(j => j.Id == jobId) > 0;
            if (removed) await PersistAsync(ct);
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveJobStateAsync(string jobId, Action<CronJobState> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureCacheAsync(ct);
            var job = _cache!.Find(j => j.Id == jobId);
            if (job is null) return;

            mutate(job.State);
            await PersistAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisableAsync(string jobId, CancellationToken ct = default)
    {
        await UpdateAsync(jobId, j => j.Enabled = false, ct);
    }

    private async Task EnsureCacheAsync(CancellationToken ct)
    {
        if (_cache is not null) return;

        if (!File.Exists(filePath))
        {
            _cache = [];
            return;
        }

        await using var stream = File.OpenRead(filePath);
        var doc = await JsonSerializer.DeserializeAsync<CronStoreDocument>(stream, JsonOptions, ct);
        _cache = doc?.Jobs ?? [];
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null) Directory.CreateDirectory(dir);

        var doc = new CronStoreDocument { Version = 1, Jobs = _cache! };
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, doc, JsonOptions, ct);
    }

    private sealed class CronStoreDocument
    {
        public int Version { get; set; } = 1;
        public List<CronJob> Jobs { get; set; } = [];
    }
}
