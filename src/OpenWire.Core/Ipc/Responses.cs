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

public sealed class ConnectionsResponse : IpcMessage
{
    public List<ConnectionInfo> Connections { get; set; } = new();
}

public sealed class HardwareResponse : IpcMessage
{
    public HardwareSnapshot Hardware { get; set; } = new();
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
