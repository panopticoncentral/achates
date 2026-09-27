using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Achates.Server.Tools;

internal static class MonetaProcess
{
    internal static async Task<string> RunAsync(string executable, string database, string request,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--database");
        process.StartInfo.ArgumentList.Add(database);
        process.Start();
        using var registration = token.Register(() => Kill(process));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, 262_145, deadline);
            var stderr = ReadBoundedAsync(process.StandardError, 8192, deadline);
            await process.StandardInput.WriteAsync(request.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token));
            token.ThrowIfCancellationRequested();
            var output = await stdout;
            if (Encoding.UTF8.GetByteCount(output) > 262_145)
                throw new InvalidDataException("Moneta response exceeds its size limit.");
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("protocol_version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
                (process.ExitCode != 0 && !root.TryGetProperty("error", out _)))
                throw new InvalidDataException("Invalid Moneta response.");
            return output.Trim();
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, int limit, CancellationTokenSource deadline)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), deadline.Token);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > limit)
            {
                deadline.Cancel();
                throw new InvalidDataException("Moneta output exceeds its size limit.");
            }
            builder.Append(buffer, 0, read);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
