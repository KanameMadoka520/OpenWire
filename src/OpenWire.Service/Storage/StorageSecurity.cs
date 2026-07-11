using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenWire.Service.Storage;

/// <summary>Validates local storage paths and applies a private Windows DACL.</summary>
[SupportedOSPlatform("windows")]
public static class StorageSecurity
{
    public static string PrepareDataDirectory(string selectedDirectory)
    {
        string selected = NormalizeLocalDirectory(selectedDirectory);
        string leaf = Path.GetFileName(selected);
        string dataDirectory = leaf.Equals("OpenWire", StringComparison.OrdinalIgnoreCase)
            ? selected
            : Path.Combine(selected, "OpenWire");
        return EnsurePrivateDirectory(dataDirectory);
    }

    public static string NormalizeLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Storage directory is required.", nameof(path));

        string full = Path.GetFullPath(path.Trim());
        if (!Path.IsPathFullyQualified(full)
            || full.StartsWith(@"\\", StringComparison.Ordinal)
            || full.StartsWith(@"\\?\", StringComparison.Ordinal)
            || full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new IOException("Storage must use a fully-qualified local file-system path.");
        }

        string root = Path.GetPathRoot(full)
            ?? throw new IOException("Storage path has no local volume root.");
        if (string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A volume root cannot be used as the OpenWire data directory.");
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new IOException("Network-backed storage is not allowed for the privileged engine.");

        RejectExistingReparsePoints(full, root);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string EnsurePrivateDirectory(string path)
    {
        string full = NormalizeLocalDirectory(path);
        Directory.CreateDirectory(full);
        RejectExistingReparsePoints(full, Path.GetPathRoot(full)!);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddCurrentUserForNonElevatedEngine(security);
        new DirectoryInfo(full).SetAccessControl(security);
        return full;
    }

    public static void EnsurePrivateFile(string path)
    {
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing reparse-point storage file: {path}");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        AddCurrentUserForNonElevatedEngine(security);
        new FileInfo(path).SetAccessControl(security);
    }

    public static void WritePrivateTextFileAtomic(string path, string contents)
    {
        string directory = EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        string finalPath = Path.Combine(directory, Path.GetFileName(path));
        RejectReparseFile(finalPath);

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            EnsurePrivateFile(tempPath);
            File.Move(tempPath, finalPath, overwrite: true);
            EnsurePrivateFile(finalPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static void RejectReparseFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing reparse-point storage file: {path}");
    }

    private static void RejectExistingReparsePoints(string full, string root)
    {
        string relative = Path.GetRelativePath(root, full);
        string current = root;
        foreach (string part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Storage path traverses a reparse point: {current}");
        }
    }

    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void AddCurrentUserForNonElevatedEngine(FileSystemSecurity security)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null
                || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;

            if (security is DirectorySecurity directory)
                AddDirectoryRule(directory, identity.User);
            else
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
        }
        catch
        {
            // SYSTEM/Administrators remain the fail-closed production ACL.
        }
    }
}
