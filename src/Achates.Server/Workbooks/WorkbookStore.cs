using System.Security.Cryptography;
using System.Text.Json;
using Achates.Providers.Completions.Content;

namespace Achates.Server.Workbooks;

/// <summary>Session-scoped originals survive transcript compaction; removed with the session.</summary>
internal sealed class WorkbookStore(string directory)
{
    public static string Id(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async Task SaveAsync(IEnumerable<CompletionWorkbookContent> workbooks, CancellationToken ct)
    {
        foreach (var workbook in workbooks)
        {
            var bytes = Convert.FromBase64String(workbook.Data);
            if (Id(bytes) != workbook.WorkbookId) throw new InvalidDataException("Workbook identifier does not match its contents.");
            Directory.CreateDirectory(directory);
            await WriteAtomicAsync(PathFor(workbook.WorkbookId, ".xlsx"), bytes, ct);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new WorkbookInfo(workbook.WorkbookId, workbook.FileName, workbook.Preview));
            await WriteAtomicAsync(PathFor(workbook.WorkbookId, ".json"), metadata, ct);
        }
    }

    public async Task<IReadOnlyList<WorkbookInfo>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<WorkbookInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            var info = JsonSerializer.Deserialize<WorkbookInfo>(await File.ReadAllBytesAsync(file, ct));
            if (info is not null) result.Add(info);
        }
        return result;
    }

    public async Task<WorkbookInfo> GetInfoAsync(string id, CancellationToken ct) =>
        JsonSerializer.Deserialize<WorkbookInfo>(await File.ReadAllBytesAsync(PathFor(id, ".json"), ct))
        ?? throw new InvalidDataException("Workbook metadata is unavailable.");

    public Task<byte[]> ReadAsync(string id, CancellationToken ct) => File.ReadAllBytesAsync(PathFor(id, ".xlsx"), ct);

    private string PathFor(string id, string extension)
    {
        if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid workbook_id. Use an ID returned by the workbook tool.");
        return Path.Combine(directory, id.ToLowerInvariant() + extension);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal sealed record WorkbookInfo(string Id, string FileName, string Preview);
}
