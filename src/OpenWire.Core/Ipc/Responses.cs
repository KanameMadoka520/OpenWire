using OpenWire.Core.Models;

namespace OpenWire.Core.Ipc;

// Responses sent from the engine back to the UI, correlated to a request.

public sealed class HelloResponse : IpcMessage
{
    public string EngineVersion { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public bool CanEnforceFirewall { get; set; }
    public bool GeoIpAvailable { get; set; }
}

public sealed class StatusResponse : IpcMessage
{
    public EngineStatus Status { get; set; } = new();
}

public sealed class GraphResponse : IpcMessage
{
    public TrafficSeries Series { get; set; } = new();
}

public sealed class UsageResponse : IpcMessage
{
    public UsageGroupBy GroupBy { get; set; }
    public GraphRange Range { get; set; }
    public List<AppUsage> Apps { get; set; } = new();
    public List<HostUsage> Hosts { get; set; } = new();
    public List<TrafficTypeUsage> Types { get; set; } = new();
    public List<CountryUsage> Countries { get; set; } = new();
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
}

public sealed class InsightsResponse : IpcMessage
{
    public InsightsReport Report { get; set; } = new();
}

public sealed class ConnectionsResponse : IpcMessage
{
    public List<ConnectionInfo> Connections { get; set; } = new();
}

public sealed class HardwareResponse : IpcMessage
{
    public HardwareSnapshot Hardware { get; set; } = new();
}

public sealed class StorageInfoResponse : IpcMessage
{
    public StorageInfo Storage { get; set; } = new();
}

public sealed class FirewallResponse : IpcMessage
{
    public FirewallStatus Status { get; set; } = new();
    public List<AppFirewallRule> Rules { get; set; } = new();
    public List<FirewallProfile> Profiles { get; set; } = new();
}

public sealed class AlertsResponse : IpcMessage
{
    public List<Alert> Alerts { get; set; } = new();
}

public sealed class DevicesResponse : IpcMessage
{
    public List<Device> Devices { get; set; } = new();
}

public sealed class SettingsResponse : IpcMessage
{
    public AppSettings Settings { get; set; } = new();
}

/// <summary>Launch-at-logon state: whether the task exists/enabled, whether the current user can
/// elevate, the task's last run result, and a human-readable message on failure.</summary>
/// <summary>Current GeoIP database status, and — for an update request — the result of the attempt.</summary>
public sealed class GeoIpStatusResponse : IpcMessage
{
    public bool Available { get; set; }
    /// <summary>Human-readable source, e.g. "DB-IP Lite" or "MaxMind GeoLite2".</summary>
    public string Source { get; set; } = "";
    /// <summary>Database build date ("yyyy-MM-dd"), or empty when unknown.</summary>
    public string BuildDate { get; set; } = "";
    /// <summary>Raw MaxMind metadata database type (e.g. "DBIP-Country-Lite").</summary>
    public string DatabaseType { get; set; } = "";
    /// <summary>Unix seconds of the last successful in-app check/update (0 = never).</summary>
    public long LastUpdateUnix { get; set; }
    public bool AutoUpdate { get; set; }

    // Meaningful only on a reply to UpdateGeoIpRequest:
    /// <summary>False when the update attempt failed (network/validation). A status query is always true.</summary>
    public bool Success { get; set; } = true;
    /// <summary>True when a newer database was actually installed (vs. already up to date).</summary>
    public bool Updated { get; set; }
    /// <summary>Short human-readable result or error detail.</summary>
    public string Message { get; set; } = "";
}

/// <summary>Generic success acknowledgement for commands.</summary>
public sealed class OkResponse : IpcMessage
{
    public bool Success { get; set; } = true;
    public string? Message { get; set; }
}

public sealed class ErrorResponse : IpcMessage
{
    public string Error { get; set; } = string.Empty;
}
