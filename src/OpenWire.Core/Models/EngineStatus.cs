namespace OpenWire.Core.Models;

/// <summary>
/// Snapshot of the engine's headline state — everything the top status bar and
/// the overview widgets need in one object.
/// </summary>
public sealed class EngineStatus
{
    public string MachineName { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;

    public FirewallMode FirewallMode { get; set; }

    /// <summary>Instantaneous download rate in bytes per second.</summary>
    public double DownloadBytesPerSec { get; set; }

    /// <summary>Instantaneous upload rate in bytes per second.</summary>
    public double UploadBytesPerSec { get; set; }

    /// <summary>Total bytes received since monitoring began (all history).</summary>
    public long TotalBytesIn { get; set; }

    /// <summary>Total bytes sent since monitoring began (all history).</summary>
    public long TotalBytesOut { get; set; }

    /// <summary>Total bytes to/from the internet (WAN) this session.</summary>
    public long TotalWanBytes { get; set; }

    /// <summary>Total bytes to/from the local network (LAN) this session.</summary>
    public long TotalLanBytes { get; set; }

    public int ActiveAppCount { get; set; }
    public int ActiveConnectionCount { get; set; }
    public int OnlineDeviceCount { get; set; }
    public int UnreadAlertCount { get; set; }

    public DateTimeOffset MonitoringSince { get; set; }

    public DataPlan DataPlan { get; set; } = new();

    /// <summary>True if the engine has the privileges/driver to actually block traffic.</summary>
    public bool CanEnforceFirewall { get; set; }

    /// <summary>True while a global network lock-down (block-all) is engaged.</summary>
    public bool LockdownActive { get; set; }
}
