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

    // Which alert kinds are enabled. Absent key = enabled.
    public Dictionary<AlertKind, bool> EnabledAlerts { get; set; } = new();

    // Security monitors
    public bool MonitorNewDevices { get; set; } = true;
    public bool MonitorDnsChanges { get; set; } = true;
    public bool MonitorArpSpoofing { get; set; } = true;
    public bool MonitorHostsFile { get; set; } = true;
    public bool MonitorRdp { get; set; } = true;

    /// <summary>Whether reverse DNS lookups are performed for remote endpoints.</summary>
    public bool ResolveHostNames { get; set; } = true;

    /// <summary>Whether GeoIP country lookups are performed.</summary>
    public bool ResolveGeoIp { get; set; } = true;

    public bool IsAlertEnabled(AlertKind kind) =>
        !EnabledAlerts.TryGetValue(kind, out var enabled) || enabled;
}
