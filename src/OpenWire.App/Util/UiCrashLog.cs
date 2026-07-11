using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OpenWire.App.Util;

/// <summary>Best-effort, private and size-bounded log for unhandled WPF dispatcher exceptions.</summary>
[SupportedOSPlatform("windows")]
internal static class UiCrashLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int BackupCount = 3;
    private const int MaxEntryChars = 64 * 1024;
    private const int MinWriteIntervalMs = 1000;

    private static readonly object Gate = new();
    private static long _lastWriteTick;
    private static int _suppressed;

    internal static void Write(Exception exception, string? localAppDataOverride = null)
    {
        lock (Gate)
        {
            long nowTick = Environment.TickCount64;
            if (_lastWriteTick != 0 && nowTick - _lastWriteTick < MinWriteIntervalMs)
            {
                _suppressed++;
                return;
            }

            int suppressed = _suppressed;
            _suppressed = 0;
            _lastWriteTick = nowTick;

            string directory = EnsurePrivateLogDirectory(localAppDataOverride);
            string logPath = Path.Combine(directory, "ui.log");
            string detail = exception.ToString();
            if (detail.Length > MaxEntryChars)
                detail = detail[..MaxEntryChars] + "\n[entry truncated]";
            string prefix = suppressed > 0
                ? $"{DateTimeOffset.Now:O}  [suppressed {suppressed} exceptions]\n"
                : string.Empty;
            string entry = prefix + $"{DateTimeOffset.Now:O}  {detail}\n\n";

            RejectReparseFile(logPath);
            long currentBytes = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
            if (currentBytes + Encoding.UTF8.GetByteCount(entry) > MaxFileBytes)
                Rotate(directory);

            using (var stream = new FileStream(
                       logPath,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(entry);
            }
            EnsurePrivateFile(logPath);
        }
    }

    private static string EnsurePrivateLogDirectory(string? localAppDataOverride)
    {
        string root = localAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("Local application data is unavailable.");

        root = Path.GetFullPath(root);
        if (root.StartsWith(@"\\", StringComparison.Ordinal)
            || root.StartsWith(@"\\?\", StringComparison.Ordinal)
            || root.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new IOException("The UI log requires a local file-system path.");
        }

        string appDirectory = Path.GetFullPath(Path.Combine(root, "OpenWire"));
        string directory = Path.GetFullPath(Path.Combine(appDirectory, "Logs"));
        string relative = Path.GetRelativePath(root, directory);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The UI log path escaped the local application data directory.");

        RejectExistingReparsePoints(root, directory);
        Directory.CreateDirectory(appDirectory);
        RejectExistingReparsePoints(root, appDirectory);
        SetPrivateDirectoryAcl(appDirectory);
        Directory.CreateDirectory(directory);
        RejectExistingReparsePoints(root, directory);
        SetPrivateDirectoryAcl(directory);
        return directory;
    }

    private static void SetPrivateDirectoryAcl(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, user);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void Rotate(string directory)
    {
        for (int index = BackupCount; index >= 1; index--)
        {
            string source = index == 1
                ? Path.Combine(directory, "ui.log")
                : Path.Combine(directory, $"ui.{index - 1}.log");
            string destination = Path.Combine(directory, $"ui.{index}.log");
            RejectReparseFile(source);
            RejectReparseFile(destination);
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }
    }

    private static void EnsurePrivateFile(string path)
    {
        RejectReparseFile(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current Windows user SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFileRule(security, user);
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void RejectExistingReparsePoints(string root, string target)
    {
        if (Directory.Exists(root)
            && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"UI log root is a reparse point: {root}");

        string current = root;
        string relative = Path.GetRelativePath(root, target);
        foreach (string part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"UI log path traverses a reparse point: {current}");
        }
    }

    private static void RejectReparseFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing reparse-point UI log file: {path}");
    }

    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void AddFileRule(FileSecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
}
