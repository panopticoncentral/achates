using System.Diagnostics;

namespace Achates.Console;

internal static class ProcessHelper
{
    public static bool ExistsOnPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(
                OperatingSystem.IsWindows() ? "where" : "which", command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
