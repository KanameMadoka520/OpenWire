using OpenWire.Core.Models;
using System.Text.Json.Serialization;

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

/// <summary>Fetch the computed analytics report (patterns + anomalies) for a range.</summary>
public sealed class GetInsightsRequest : IpcMessage
{
    public GraphRange Range { get; set; } = GraphRange.Week;

    /// <summary>Custom window bounds (unix seconds). When both are set and To &gt; From, the
    /// report covers [From, To] instead of the preset <see cref="Range"/> ending now.</summary>
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
}

/// <summary>Fetch the current live connection table.</summary>
public sealed class GetConnectionsRequest : IpcMessage { }

/// <summary>Fetch current hardware telemetry. Matching stream/sequence values request only
/// history points added since the previous response; details can be sampled less often.</summary>
public sealed class GetHardwareRequest : IpcMessage
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? HistoryStreamId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AfterHistorySequence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OmitDetails { get; set; }
}

/// <summary>Fetch firewall status, per-app rules and profiles.</summary>
public sealed class GetFirewallRequest : IpcMessage { }

/// <summary>Change the firewall mode (Off / ClickToBlock / AskToConnect).</summary>
public sealed class SetFirewallModeRequest : IpcMessage
{
    [JsonRequired]
    public FirewallMode Mode { get; set; }
}

/// <summary>Engage or lift a global network lock-down (block every app in both directions). Overlays
/// the current firewall mode; the per-app rules are untouched and resume when the lock-down is lifted.</summary>
public sealed class SetLockdownRequest : IpcMessage
{
    [JsonRequired]
    public bool On { get; set; }

    /// <summary>When engaging, auto-lift the lock-down after this many seconds (0 = stay until
    /// manually lifted). Ignored when <see cref="On"/> is false.</summary>
    public long DurationSeconds { get; set; }
}

/// <summary>Block or unblock an application in a given direction.</summary>
public sealed class SetAppBlockedRequest : IpcMessage
{
    [JsonRequired]
    public string AppId { get; set; } = string.Empty;
    [JsonRequired]
    public string ExecutablePath { get; set; } = string.Empty;
    [JsonRequired]
    public bool BlockIncoming { get; set; }
    [JsonRequired]
    public bool BlockOutgoing { get; set; }
}

/// <summary>Create or update a firewall profile (matched by name).</summary>
public sealed class SaveFirewallProfileRequest : IpcMessage
{
    [JsonRequired]
    public FirewallProfile Profile { get; set; } = new();
}

/// <summary>Delete a firewall profile by name (the Default profile cannot be deleted).</summary>
public sealed class DeleteFirewallProfileRequest : IpcMessage
{
    [JsonRequired]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Switch the active firewall profile, applying its mode and blocked apps.</summary>
public sealed class ActivateFirewallProfileRequest : IpcMessage
{
    [JsonRequired]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Answer an Ask-to-Connect prompt for a pending app.</summary>
public sealed class ResolveAppDecisionRequest : IpcMessage
{
    [JsonRequired]
    public string AppId { get; set; } = string.Empty;
    [JsonRequired]
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
    [JsonRequired]
    public string DeviceId { get; set; } = string.Empty;
    [JsonRequired]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Forget a LAN device (removes it from the list).</summary>
public sealed class ForgetDeviceRequest : IpcMessage
{
    [JsonRequired]
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>Fetch the storage location + database size.</summary>
public sealed class GetStorageInfoRequest : IpcMessage { }

/// <summary>Relocate the database to a new directory (takes effect after restart).</summary>
public sealed class SetStorageLocationRequest : IpcMessage
{
    [JsonRequired]
    public string NewDirectory { get; set; } = string.Empty;
}

/// <summary>Clear recorded data (per-mode) and compact the database.</summary>
public sealed class ClearDataRequest : IpcMessage
{
    [JsonRequired]
    public ClearDataMode Mode { get; set; } = ClearDataMode.MinuteHistory;
}

/// <summary>Fetch persisted settings.</summary>
public sealed class GetSettingsRequest : IpcMessage { }

/// <summary>Persist updated settings.</summary>
public sealed class SetSettingsRequest : IpcMessage
{
    [JsonRequired]
    public AppSettings Settings { get; set; } = new();
}

/// <summary>Tell the engine whether the UI is actively being viewed, so it can throttle the
/// high-frequency hardware / per-process samplers when the app is minimized to the tray.</summary>
public sealed class SetUiActiveRequest : IpcMessage
{
    [JsonRequired]
    public bool Active { get; set; } = true;
}

/// <summary>Fetch per-blocklist runtime status (cache freshness, entry counts, fetch errors).</summary>
public sealed class GetBlocklistStatusRequest : IpcMessage { }

/// <summary>Re-download enabled blocklists now (one list when <see cref="ListId"/> is set,
/// otherwise all). The download runs in the background; poll status to see it finish.</summary>
public sealed class RefreshBlocklistsRequest : IpcMessage
{
    public string? ListId { get; set; }
}

/// <summary>Fetch the current GeoIP database status (source, build date, last update).</summary>
public sealed class GetGeoIpStatusRequest : IpcMessage { }

/// <summary>Download and install the latest free GeoIP country database (DB-IP Lite). The reply is
/// a <see cref="GeoIpStatusResponse"/> once the download finishes.</summary>
public sealed class UpdateGeoIpRequest : IpcMessage { }
