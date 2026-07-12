namespace OpenWire.Core.Models;

/// <summary>Direction of network traffic relative to this machine.</summary>
public enum TrafficDirection
{
    Incoming = 0,
    Outgoing = 1,
}

/// <summary>Transport protocol of a connection.</summary>
public enum TransportProtocol
{
    Tcp = 0,
    Udp = 1,
    Other = 2,
}

/// <summary>
/// Firewall operating mode, mirroring GlassWire's three modes.
/// </summary>
public enum FirewallMode
{
    /// <summary>OpenWire does not block anything; monitoring only.</summary>
    Off = 0,

    /// <summary>Everything is allowed by default; the user clicks an app to block it.</summary>
    ClickToBlock = 1,

    /// <summary>New apps are blocked until the user approves them (deny-by-default prompt).</summary>
    AskToConnect = 2,
}

/// <summary>Per-application firewall decision.</summary>
public enum AppFirewallStatus
{
    /// <summary>Allowed to reach the network.</summary>
    Allowed = 0,

    /// <summary>Blocked by an OpenWire firewall rule.</summary>
    Blocked = 1,

    /// <summary>New / unknown app awaiting a decision (Ask-to-Connect mode).</summary>
    Pending = 2,
}

/// <summary>Time window shown on the graph / used for usage aggregation.</summary>
public enum GraphRange
{
    FiveMinutes = 0,
    ThreeHours = 1,
    Day = 2,
    Week = 3,
    Month = 4,
    Unlimited = 5,
}

/// <summary>How usage totals are grouped on the Usage screen.</summary>
public enum UsageGroupBy
{
    Apps = 0,
    Hosts = 1,
    TrafficType = 2,
    Country = 3,
}

/// <summary>Kinds of alerts OpenWire can raise (mirrors GlassWire's alert catalogue).</summary>
public enum AlertKind
{
    /// <summary>An application accessed the network for the first time.</summary>
    NewApp = 0,

    /// <summary>A known application's binary/version/signature changed.</summary>
    AppInfoChanged = 1,

    /// <summary>A new device joined the local network.</summary>
    NewDevice = 2,

    /// <summary>A device left the local network.</summary>
    DeviceLeft = 3,

    /// <summary>The system's DNS server changed.</summary>
    DnsServerChanged = 4,

    /// <summary>Possible ARP spoofing detected (duplicate MAC for gateway, etc.).</summary>
    ArpSpoofing = 5,

    /// <summary>An incoming Remote Desktop (RDP) connection was observed.</summary>
    RdpConnection = 6,

    /// <summary>The monthly data plan limit (or a threshold) was reached.</summary>
    DataLimitReached = 7,

    /// <summary>An application was blocked by the firewall.</summary>
    AppBlocked = 8,

    /// <summary>The hosts file was modified.</summary>
    HostsFileChanged = 9,

    /// <summary>A blocked application attempted to reach the network.</summary>
    BlockedAppAttempt = 10,

    /// <summary>General informational event.</summary>
    Info = 11,

    /// <summary>A statistical usage anomaly (traffic spike, upload-heavy app, new country, odd-hour activity).</summary>
    UsageAnomaly = 12,

    /// <summary>The system internet proxy configuration changed.</summary>
    ProxySettingsChanged = 13,

    /// <summary>Internet connectivity was lost or restored (connectivity-state change).</summary>
    InternetAccessChanged = 14,

    /// <summary>An application communicated with a host found on a subscribed blocklist.</summary>
    SuspiciousHost = 15,

    /// <summary>An application approached or reached its per-app data quota.</summary>
    DataQuotaReached = 16,
}

/// <summary>Reset cadence of a per-application data quota.</summary>
public enum QuotaPeriod
{
    /// <summary>Resets at local midnight each day.</summary>
    Daily = 0,

    /// <summary>Resets at local midnight each Monday.</summary>
    Weekly = 1,

    /// <summary>Resets at local midnight on the first day of each month.</summary>
    Monthly = 2,
}

/// <summary>Severity used for colour-coding alerts.</summary>
public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Best-effort classification of a LAN device.</summary>
public enum DeviceKind
{
    Unknown = 0,
    ThisComputer = 1,
    Router = 2,
    Computer = 3,
    Phone = 4,
    Tablet = 5,
    Television = 6,
    GameConsole = 7,
    Printer = 8,
    Camera = 9,
    SmartHome = 10,
    Server = 11,
}
