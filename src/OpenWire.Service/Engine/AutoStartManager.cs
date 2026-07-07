using System.Diagnostics;
using System.Runtime.Versioning;
using OpenWire.Core.Ipc;

namespace OpenWire.Service.Engine;

/// <summary>
/// Registers / removes a "launch OpenWire at logon" entry. A plain Run-key or Startup-folder
/// shortcut can't start an elevated process without a UAC prompt each time, so this uses a Task
/// Scheduler logon task with <c>/RL HIGHEST</c> (runs elevated, no stored password) that launches
/// the app; the app then spawns the engine with no further prompt. Creating such a task needs
/// elevation, which the engine already has.
///
/// Existence is checked by exit code (not by parsing schtasks' localized output), so it works on
/// any Windows UI language.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoStartManager
{
    private const string TaskName = @"OpenWire\Autostart";

    public static AutoStartStatusResponse Query()
    {
        bool exists = Run("/Query /TN \"" + TaskName + "\"", out _) == 0;
        return new AutoStartStatusResponse { Exists = exists, Enabled = exists, CanElevate = true };
    }

    /// <summary>Create or delete the logon task. The caller (app) has already verified the user can
    /// elevate; here we just apply it and report the real outcome.</summary>
    public static AutoStartStatusResponse Configure(bool enabled, string appExePath, string userName)
    {
        try
        {
            if (!enabled)
            {
                Run("/Delete /TN \"" + TaskName + "\" /F", out _); // ignore "not found"
                return Query();
            }

            if (string.IsNullOrWhiteSpace(appExePath) || !File.Exists(appExePath))
                return Fail("The app executable could not be located.");

            // /TR must quote the exe so a Program-Files space doesn't split the action.
            // /IT + /RU <user> with no /RP = interactive token, passwordless; /RL HIGHEST = elevated.
            string ru = string.IsNullOrWhiteSpace(userName) ? "" : $" /RU \"{userName}\"";
            string args =
                $"/Create /TN \"{TaskName}\" /TR \"\\\"{appExePath}\\\"\" /SC ONLOGON /RL HIGHEST /IT{ru} /F";

            int code = Run(args, out string output);
            if (code != 0)
                return Fail(string.IsNullOrWhiteSpace(output) ? $"schtasks failed (code {code})." : output.Trim());

            return Query();
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static AutoStartStatusResponse Fail(string message) =>
        new() { Exists = false, Enabled = false, CanElevate = true, Message = message };

    private static int Run(string arguments, out string output)
    {
        output = "";
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
