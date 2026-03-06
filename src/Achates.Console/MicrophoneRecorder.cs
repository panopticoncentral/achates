using System.Diagnostics;

namespace Achates.Console;

/// <summary>
/// Records audio from the microphone using sox/rec, stopping when silence is detected.
/// </summary>
internal static class MicrophoneRecorder
{
    private const int SampleRate = 24000;
    private const int Channels = 1;

    /// <summary>
    /// Records from the default microphone until silence is detected.
    /// Returns the captured audio as a WAV byte array, or null if recording failed.
    /// </summary>
    public static byte[]? Record(CancellationToken cancellationToken)
    {
        if (!ProcessHelper.ExistsOnPath("rec"))
        {
            System.Console.Error.WriteLine("No recording tool found. Install sox (brew install sox / apt install sox).");
            return null;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"achates-rec-{Guid.NewGuid():N}.wav");

        try
        {
            // Let rec capture at whatever the mic natively supports, then use sox's
            // output format flags to convert to 24kHz mono 16-bit WAV on the fly.
            // -b 16 on the output side forces 16-bit PCM (sox defaults to 32-bit when resampling).
            var fullArgs = $"-q -b 16 -t wav {tempFile} rate {SampleRate} channels {Channels} silence 1 0.1 1% 1 2.0 3%";

            var psi = new ProcessStartInfo("rec", fullArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                System.Console.Error.WriteLine("Failed to start recording process.");
                return null;
            }

            using var registration = cancellationToken.Register(() =>
            {
                try { process.Kill(); } catch { }
            });

            process.WaitForExit();

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                System.Console.Error.WriteLine($"Recording failed (exit {process.ExitCode}): {stderr}");
                return null;
            }

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                System.Console.Error.WriteLine("Recording produced no output.");
                return null;
            }

            return File.ReadAllBytes(tempFile);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
