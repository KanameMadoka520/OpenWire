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

/// <summary>A single OS process belonging to an application (for the firewall tree).</summary>
public sealed class AppProcess
{
    public int Pid { get; set; }
    public double DownRate { get; set; }
    public double UpRate { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
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

    /// <summary>Instantaneous throughput, bytes/sec.</summary>
    public double DownRate { get; set; }
    public double UpRate { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public AppFirewallStatus FirewallStatus { get; set; } = AppFirewallStatus.Allowed;

    /// <summary>Number of currently-open connections owned by this app.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>True if the app has moved any bytes within the last few seconds.</summary>
    public bool IsActive { get; set; }

    /// <summary>Distinct remote hosts contacted in the window.</summary>
    public int HostCount { get; set; }

    /// <summary>The most-contacted remote host (resolved name or IP) + its country.</summary>
    public string PrimaryHost { get; set; } = string.Empty;
    public string PrimaryHostCountry { get; set; } = string.Empty;

    /// <summary>Child processes (PIDs) of this app, for the expandable firewall row.</summary>
    public List<AppProcess> Processes { get; set; } = new();

    /// <summary>
    /// Rolling per-second throughput history (bytes/sec, combined down+up, oldest→newest,
    /// last ~60 s) that drives the per-app sparkline. Replaced wholesale each tick
    /// (copy-on-write) so readers on the IPC thread never see a mutating list.
    /// </summary>
    public List<int> RateHistory { get; set; } = new();

    /// <summary>Optional VirusTotal file-reputation verdict for the app's binary.</summary>
    public AppReputation? Reputation { get; set; }
}
