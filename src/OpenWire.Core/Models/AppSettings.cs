namespace OpenWire.Core.Models;

/// <summary>User-configurable engine + UI settings, persisted by the service.</summary>
public sealed class AppSettings
{
    public FirewallMode FirewallMode { get; set; } = FirewallMode.Off;

    public bool LaunchOnStartup { get; set; } = true;
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

    // Security monitors
    public bool MonitorNewDevices { get; set; } = true;
    public bool MonitorDnsChanges { get; set; } = true;
    public bool MonitorArpSpoofing { get; set; } = true;
    public bool MonitorHostsFile { get; set; } = true;
    public bool MonitorRdp { get; set; } = true;

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
    /// Optional VirusTotal API key (the user's own free key). When set, the engine
    /// hashes application binaries and shows a per-app reputation column. Empty =
    /// the integration is off. Never bundled — supplied by the user in Settings.
    /// </summary>
    public string VirusTotalApiKey { get; set; } = string.Empty;

    public bool IsAlertEnabled(AlertKind kind) =>
        !EnabledAlerts.TryGetValue(kind, out var enabled) || enabled;
}
