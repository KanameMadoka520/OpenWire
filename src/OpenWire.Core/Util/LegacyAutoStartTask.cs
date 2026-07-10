using System.Diagnostics;
using System.Runtime.Versioning;

namespace OpenWire.Core.Util;

/// <summary>Removes the legacy task that launched the WPF UI at HIGHEST integrity.</summary>
[SupportedOSPlatform("windows")]
public static class LegacyAutoStartTask
{
    private const string TaskName = @"OpenWire\Autostart";

    /// <summary>Returns true only when the legacy task existed and was successfully removed.</summary>
    public static bool RemoveIfPresent()
    {
        if (Run("/Query", "/TN", TaskName) != 0) return false;
        return Run("/Delete", "/TN", TaskName, "/F") == 0;
    }

    private static int Run(params string[] arguments)
    {
        try
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(windows, "System32", "schtasks.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = start };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(15_000))
            {
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                return process.ExitCode;
            }

            try { process.Kill(entireProcessTree: true); } catch { }
            return -1;
        }
        catch
        {
            return -1;
        }
    }
}
