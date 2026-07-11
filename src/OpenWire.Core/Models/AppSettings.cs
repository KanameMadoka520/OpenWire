namespace OpenWire.Core.Models;

/// <summary>User-configurable engine + UI settings, persisted by the service.</summary>
public sealed class AppSettings
{
    public FirewallMode FirewallMode { get; set; } = FirewallMode.Off;

    /// <summary>Start the non-elevated OpenWire UI from the current user's Run key at logon.</summary>
    public bool LaunchOnStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowDesktopNotifications { get; set; } = true;

    /// <summary>UI theme name ("Dark" / "Light").</summary>
    public string Theme { get; set; } = "Dark";

    public DataPlan DataPlan { get; set; } = new();

    /// <summary>Named firewall profiles. Always contains at least "Default".</summary>
    public List<FirewallProfile> FirewallProfiles { get; set; } = new();

    /// <summary>Name of the currently active firewall profile.</summary>
    public string ActiveProfile { get; set; } = "Default";

    // Which alert kinds are enabled. Absent key = enabled.
    public Dictionary<AlertKind, bool> EnabledAlerts { get; set; } = new();

    /// <summary>Automatically rescan the LAN on a schedule (the Scanner's auto-scan toggle).</summary>
    public bool AutoScanDevices { get; set; } = true;

    // Security monitors
    public bool MonitorNewDevices { get; set; } = true;
    public bool MonitorDnsChanges { get; set; } = true;
    public bool MonitorArpSpoofing { get; set; } = true;
    public bool MonitorHostsFile { get; set; } = true;
    public bool MonitorRdp { get; set; } = true;
    public bool MonitorProxyChanges { get; set; } = true;

    /// <summary>
    /// Detect when a known application's on-disk metadata changes — a new version,
    /// a different Authenticode signer, or a binary that lost its signature. A silent
    /// change to an already-trusted app is a classic malware-replacement / DLL-hijack
    /// vector, so it is surfaced. The file's baseline is captured the first time the app
    /// is seen; only later changes alert.
    /// </summary>
    public bool MonitorAppInfo { get; set; } = true;

    /// <summary>
    /// Watch overall internet connectivity (via the Windows Network List Manager) and
    /// raise an alert when access is lost or restored. Sudden loss can mean a dropped
    /// link, a captive portal, or a security tool cutting the connection.
    /// </summary>
    public bool MonitorInternetAccess { get; set; } = true;

    /// <summary>
    /// Detect statistical usage anomalies over recorded history (per-app volume
    /// spikes vs a rolling baseline, upload-heavy apps, first contact with a new
    /// country, odd-hour activity) and raise them as alerts. Monitoring/recording
    /// is OpenWire's core purpose; this turns the record into anomaly detection.
    /// </summary>
    public bool MonitorUsageAnomalies { get; set; } = true;

    /// <summary>
    /// Days of per-minute history to retain. Older minute rows are pruned to keep
    /// the database bounded; compact daily rollups are kept far longer so long-term
    /// trends and patterns survive. 0 disables pruning (keep everything).
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>Whether reverse DNS lookups are performed for remote endpoints.</summary>
    public bool ResolveHostNames { get; set; } = true;

    /// <summary>Whether GeoIP country lookups are performed.</summary>
    public bool ResolveGeoIp { get; set; } = true;

    /// <summary>
    /// Check for a newer free DB-IP Lite country database on startup (at most once every few
    /// weeks) and install it automatically. Off by default — GeoIP updates are an opt-in network
    /// call. Users can always update on demand from Settings regardless of this flag.
    /// </summary>
    public bool GeoIpAutoUpdate { get; set; } = false;

    /// <summary>Unix seconds of the last successful in-app GeoIP check/update (0 = never). Written
    /// by the engine; decoupled from the app build, so an old OpenWire keeps a fresh GeoIP DB.</summary>
    public long GeoIpLastUpdateUnix { get; set; }

    /// <summary>
    /// Optional VirusTotal API key (the user's own free key). When set, the engine
    /// hashes application binaries and shows a per-app reputation column. Empty =
    /// the integration is off. Never bundled — supplied by the user in Settings.
    /// </summary>
    public string VirusTotalApiKey { get; set; } = string.Empty;

    public bool IsAlertEnabled(AlertKind kind) =>
        !EnabledAlerts.TryGetValue(kind, out var enabled) || enabled;
}
