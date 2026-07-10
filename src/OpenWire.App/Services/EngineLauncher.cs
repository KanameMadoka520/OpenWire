using System.Diagnostics;
using System.IO;

namespace OpenWire.App.Services;

/// <summary>Locates and spawns the OpenWire engine (OpenWire.Service.exe). The engine's manifest is
/// requireAdministrator, so the non-elevated app raises a UAC prompt. The app passes
/// <c>--parent-app &lt;pid&gt;</c> so the engine authorizes only this UI and exits with it.</summary>
public static class EngineLauncher
{
    public static string? LocateServiceExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "OpenWire.Service.exe");
        if (File.Exists(local)) return local;

        string config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "OpenWire.Service", "bin", config, "net9.0-windows", "OpenWire.Service.exe"));
        return File.Exists(dev) ? dev : null;
    }

    /// <summary>Start the engine tied to this app's lifetime. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> with NativeErrorCode 1223 if the user
    /// declines the UAC prompt.</summary>
    public static bool SpawnService(bool runas)
    {
        string? exe = LocateServiceExe();
        if (exe is null) return false;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Arguments = $"--parent-app {Environment.ProcessId}",
        };
        if (runas) psi.Verb = "runas";
        Process.Start(psi);
        return true;
    }
}
