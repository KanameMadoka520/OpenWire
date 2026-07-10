using System.IO;
using Microsoft.Win32;

namespace OpenWire.App.Services;

/// <summary>Per-user, non-elevated launch-at-logon registration for the WPF app.</summary>
public static class UserAutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenWire";

    public static bool IsEnabled()
    {
        string? expected = ExpectedCommand();
        if (expected is null) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return string.Equals(key?.GetValue(ValueName) as string, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Enabled, string? Error) Configure(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string command = ExpectedCommand()
                    ?? throw new FileNotFoundException("The OpenWire app executable could not be located.");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            bool actual = IsEnabled();
            return (actual, actual == enabled ? null : "Windows did not retain the requested startup setting.");
        }
        catch (Exception ex)
        {
            return (IsEnabled(), ex.Message);
        }
    }

    private static string? ExpectedCommand()
    {
        string? path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : $"\"{path}\"";
    }
}
