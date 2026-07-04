namespace OpenWire.Core.Models;

/// <summary>
/// Identity + metadata for an executable that has produced network activity.
/// The <see cref="Id"/> is a stable key derived from the normalized executable path.
/// </summary>
public sealed class AppInfo
{
    /// <summary>Stable identifier (lower-cased full path, or "system"/"unknown").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Friendly display name (product name, else file description, else file name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full path to the executable on disk.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Publisher / Authenticode signer subject, if signed.</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>File / product version string.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Short description from the binary's version resource.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Base64-encoded PNG of the extracted 32px application icon (optional).</summary>
    public string? IconPngBase64 { get; set; }

    /// <summary>True if the binary carries a valid Authenticode signature.</summary>
    public bool IsSigned { get; set; }

    public static AppInfo System { get; } = new() { Id = "system", Name = "System", Description = "Windows kernel & system", Publisher = "Microsoft Windows" };

    public static AppInfo Unknown { get; } = new() { Id = "unknown", Name = "Unknown", Description = "Unidentified process" };
}

/// <summary>
/// Aggregated network usage for a single application over some window,
/// plus its current firewall status and live activity.
/// </summary>
public sealed class AppUsage
{
    public AppInfo App { get; set; } = new();

    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public AppFirewallStatus FirewallStatus { get; set; } = AppFirewallStatus.Allowed;

    /// <summary>Number of currently-open connections owned by this app.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>True if the app has moved any bytes within the last few seconds.</summary>
    public bool IsActive { get; set; }

    /// <summary>Distinct remote hosts contacted in the window.</summary>
    public int HostCount { get; set; }
}
