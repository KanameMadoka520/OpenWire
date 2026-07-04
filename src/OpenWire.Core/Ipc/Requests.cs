using OpenWire.Core.Models;

namespace OpenWire.Core.Ipc;

// Requests sent from the UI to the engine. Each expects a correlated response.

/// <summary>Handshake; the engine replies with version + capabilities.</summary>
public sealed class HelloRequest : IpcMessage
{
    public string ClientVersion { get; set; } = string.Empty;
}

/// <summary>Fetch the headline <see cref="EngineStatus"/>.</summary>
public sealed class GetStatusRequest : IpcMessage { }

/// <summary>Fetch the traffic series for a graph range.</summary>
public sealed class GetGraphRequest : IpcMessage
{
    public GraphRange Range { get; set; } = GraphRange.FiveMinutes;
}

/// <summary>Start/stop the per-second live tick + event stream on this connection.</summary>
public sealed class SubscribeLiveRequest : IpcMessage
{
    public bool Subscribe { get; set; } = true;
}

/// <summary>Fetch usage aggregated over a range, grouped by apps / hosts / traffic type.</summary>
public sealed class GetUsageRequest : IpcMessage
{
    public GraphRange Range { get; set; } = GraphRange.Day;
    public UsageGroupBy GroupBy { get; set; } = UsageGroupBy.Apps;
}

/// <summary>Fetch the current live connection table.</summary>
public sealed class GetConnectionsRequest : IpcMessage { }

/// <summary>Fetch current hardware telemetry + recent history.</summary>
public sealed class GetHardwareRequest : IpcMessage { }

/// <summary>Fetch firewall status, per-app rules and profiles.</summary>
public sealed class GetFirewallRequest : IpcMessage { }

/// <summary>Change the firewall mode (Off / ClickToBlock / AskToConnect).</summary>
public sealed class SetFirewallModeRequest : IpcMessage
{
    public FirewallMode Mode { get; set; }
}

/// <summary>Block or unblock an application in a given direction.</summary>
public sealed class SetAppBlockedRequest : IpcMessage
{
    public string AppId { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool BlockIncoming { get; set; }
    public bool BlockOutgoing { get; set; }
}

/// <summary>Create or update a firewall profile (matched by name).</summary>
public sealed class SaveFirewallProfileRequest : IpcMessage
{
    public FirewallProfile Profile { get; set; } = new();
}

/// <summary>Delete a firewall profile by name (the Default profile cannot be deleted).</summary>
public sealed class DeleteFirewallProfileRequest : IpcMessage
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Switch the active firewall profile, applying its mode and blocked apps.</summary>
public sealed class ActivateFirewallProfileRequest : IpcMessage
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Answer an Ask-to-Connect prompt for a pending app.</summary>
public sealed class ResolveAppDecisionRequest : IpcMessage
{
    public string AppId { get; set; } = string.Empty;
    public bool Allow { get; set; }
    public bool Remember { get; set; } = true;
}

/// <summary>Fetch the most recent alerts.</summary>
public sealed class GetAlertsRequest : IpcMessage
{
    public int Limit { get; set; } = 200;
}

/// <summary>Acknowledge one alert, or all when <see cref="All"/> is set.</summary>
public sealed class AckAlertRequest : IpcMessage
{
    public long AlertId { get; set; }
    public bool All { get; set; }
}

/// <summary>Fetch the known LAN devices, optionally kicking off a fresh scan first.</summary>
public sealed class GetDevicesRequest : IpcMessage
{
    public bool Rescan { get; set; }
}

/// <summary>Rename a LAN device.</summary>
public sealed class RenameDeviceRequest : IpcMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>Forget a LAN device (removes it from the list).</summary>
public sealed class ForgetDeviceRequest : IpcMessage
{
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>Fetch persisted settings.</summary>
public sealed class GetSettingsRequest : IpcMessage { }

/// <summary>Persist updated settings.</summary>
public sealed class SetSettingsRequest : IpcMessage
{
    public AppSettings Settings { get; set; } = new();
}
