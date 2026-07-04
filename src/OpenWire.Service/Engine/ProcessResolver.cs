using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using OpenWire.Core.Models;
using OpenWire.Service.Native;

namespace OpenWire.Service.Engine;

/// <summary>
/// Resolves a process id to a rich <see cref="AppInfo"/> (path, display name,
/// publisher, version, signed-state) and caches the results. Icon extraction is
/// intentionally left to the UI (which owns the path) to keep GDI out of the service.
/// </summary>
public sealed class ProcessResolver
{
    // PID -> app, refreshed opportunistically; PID reuse is tolerated for a monitor.
    private readonly ConcurrentDictionary<int, AppInfo> _byPid = new();
    // Normalized path -> metadata (path-stable, expensive to compute once).
    private readonly ConcurrentDictionary<string, AppInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public AppInfo Resolve(int pid)
    {
        if (pid <= 0) return AppInfo.System;
        if (pid == 4) return AppInfo.System;

        if (_byPid.TryGetValue(pid, out var cached))
            return cached;

        var info = ResolveUncached(pid);
        _byPid[pid] = info;
        return info;
    }

    /// <summary>Return the stable AppId for a PID without building full metadata when cached.</summary>
    public string ResolveId(int pid) => Resolve(pid).Id;

    private AppInfo ResolveUncached(int pid)
    {
        string? path = ProcessNative.GetProcessImagePath(pid);

        if (string.IsNullOrEmpty(path))
        {
            // Fall back to the managed API (works for same-or-lower integrity processes).
            try
            {
                using var p = Process.GetProcessById(pid);
                path = p.MainModule?.FileName;
            }
            catch
            {
                // Protected / exited process.
            }
        }

        if (string.IsNullOrEmpty(path))
            return new AppInfo { Id = $"pid:{pid}", Name = $"Process {pid}", Description = "Unresolved process" };

        return _byPath.GetOrAdd(path, BuildFromPath);
    }

    private static AppInfo BuildFromPath(string path)
    {
        var info = new AppInfo
        {
            Id = path.ToLowerInvariant(),
            ExecutablePath = path,
            Name = Path.GetFileNameWithoutExtension(path),
        };

        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            info.Name = FirstNonEmpty(vi.ProductName, vi.FileDescription, info.Name)!.Trim();
            info.Description = (vi.FileDescription ?? string.Empty).Trim();
            info.Version = (vi.FileVersion ?? vi.ProductVersion ?? string.Empty).Trim();
        }
        catch
        {
            // No version resource.
        }

        try
        {
            // CreateFromSignedFile extracts the embedded Authenticode signer certificate;
            // there is no non-obsolete replacement for reading a PE's signature subject.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            info.IsSigned = true;
            info.Publisher = ExtractCommonName(cert.Subject);
        }
        catch
        {
            info.IsSigned = false;
        }

        return info;
    }

    private static string ExtractCommonName(string distinguishedName)
    {
        // Pull "CN=..." out of an X.500 subject; tolerate quoting/ordering.
        foreach (var part in distinguishedName.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..].Trim('"');
        }
        return distinguishedName;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Drop cache entries for PIDs that are no longer alive.</summary>
    public void PruneDeadPids(HashSet<int> alivePids)
    {
        foreach (var pid in _byPid.Keys)
        {
            if (!alivePids.Contains(pid))
                _byPid.TryRemove(pid, out _);
        }
    }
}
