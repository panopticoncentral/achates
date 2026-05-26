using System.Diagnostics;

namespace Achates.Server.Speech;

/// <summary>
/// Supervises an optional kokoro-fastapi child process. Spawns it on startup
/// (managed mode), polls its /health endpoint, and exposes availability via
/// the shared <see cref="KokoroSpeechSynthesizer"/>. On unexpected exit,
/// restarts with exponential backoff (1s → 5s → 30s → 5min, then steady).
/// When config provides only <see cref="SpeechConfig.Endpoint"/>, no child
/// is spawned — only the health check runs.
/// </summary>
public sealed class KokoroSidecarProcess(
    SpeechConfig config,
    KokoroSpeechSynthesizer synth,
    HttpClient httpClient,
    ILogger<KokoroSidecarProcess> logger) : BackgroundService
{
    private static readonly TimeSpan[] BackoffSchedule =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (config.Sidecar is null && string.IsNullOrWhiteSpace(config.Endpoint))
        {
            logger.LogInformation("Speech: tools.speech configured but neither sidecar nor endpoint provided. Speech disabled.");
            return;
        }

        if (config.Sidecar is not null && !string.IsNullOrWhiteSpace(config.Endpoint))
            logger.LogWarning("Speech: both tools.speech.sidecar and tools.speech.endpoint set; endpoint wins, sidecar will not be auto-launched.");

        var managed = config.Sidecar is not null && string.IsNullOrWhiteSpace(config.Endpoint);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (managed && !TrySpawn(out _process))
                {
                    await DelayBackoff(attempt++, ct);
                    continue;
                }

                if (await WaitForHealthyAsync(ct))
                {
                    synth.MarkAvailable(true);
                    attempt = 0;
                    logger.LogInformation("Speech: Kokoro sidecar is ready.");

                    // Block until the process exits OR shutdown is requested.
                    if (_process is not null)
                        await _process.WaitForExitAsync(ct);
                    else
                        await Task.Delay(Timeout.Infinite, ct);
                }
                else
                {
                    logger.LogError("Speech: health check timed out. Speech disabled until next attempt.");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Speech: sidecar supervisor error.");
            }
            finally
            {
                synth.MarkAvailable(false);
                TryDisposeProcess();
            }

            if (!ct.IsCancellationRequested && managed)
            {
                logger.LogWarning("Speech: sidecar exited; scheduling restart.");
                await DelayBackoff(attempt++, ct);
            }
            else if (!managed)
            {
                // External endpoint went down; re-poll after a delay.
                await DelayBackoff(attempt++, ct);
            }
        }

        TryDisposeProcess();
    }

    private bool TrySpawn(out Process? process)
    {
        process = null;
        var sidecar = config.Sidecar!;

        if (string.IsNullOrWhiteSpace(sidecar.WorkingDir) || !Directory.Exists(ExpandPath(sidecar.WorkingDir)))
        {
            logger.LogError("Speech: sidecar working_dir '{Dir}' does not exist. See docs/speech-setup.md.", sidecar.WorkingDir);
            return false;
        }

        if (string.IsNullOrWhiteSpace(sidecar.Command))
        {
            logger.LogError("Speech: sidecar command is empty in config.");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = sidecar.Command,
            WorkingDirectory = ExpandPath(sidecar.WorkingDir),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in sidecar.Args ?? [])
            psi.ArgumentList.Add(arg);

        try
        {
            var p = Process.Start(psi);
            if (p is null) return false;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogInformation("[kokoro] {Line}", e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) logger.LogWarning("[kokoro] {Line}", e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            process = p;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speech: failed to spawn sidecar process.");
            return false;
        }
    }

    private async Task<bool> WaitForHealthyAsync(CancellationToken ct)
    {
        var endpoint = ResolveEndpoint();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await httpClient.GetAsync(new Uri(endpoint, "/health"), ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* not ready yet */ }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return false;
    }

    private Uri ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            return new Uri(config.Endpoint);

        // Derive from --port in sidecar.args, default 8880.
        var args = config.Sidecar?.Args ?? [];
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--port") return new Uri($"http://127.0.0.1:{args[i + 1]}");
        }
        return new Uri("http://127.0.0.1:8880");
    }

    private static Task DelayBackoff(int attempt, CancellationToken ct)
    {
        var idx = Math.Min(attempt, BackoffSchedule.Length - 1);
        return Task.Delay(BackoffSchedule[idx], ct);
    }

    private void TryDisposeProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Speech: error cleaning up sidecar process.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string ExpandPath(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
