using OpenWire.Core.Models;

namespace OpenWire.Core.Ipc;

// Unsolicited events pushed by the engine to subscribed UI connections.

/// <summary>Once-per-second live throughput tick that drives the animated graph.</summary>
public sealed class LiveTickEvent : IpcMessage
{
    public TrafficSample Sample { get; set; } = new();
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public int ActiveConnectionCount { get; set; }
    public int ActiveAppCount { get; set; }
}

/// <summary>A new alert was raised.</summary>
public sealed class AlertRaisedEvent : IpcMessage
{
    public Alert Alert { get; set; } = new();
}

/// <summary>Ask-to-Connect: a new/unknown app wants network access and is blocked pending a decision.</summary>
public sealed class FirewallPromptEvent : IpcMessage
{
    public AppInfo App { get; set; } = new();
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
}

/// <summary>A LAN device appeared, changed, or went offline.</summary>
public sealed class DeviceChangedEvent : IpcMessage
{
    public Device Device { get; set; } = new();
    public bool Removed { get; set; }
}

/// <summary>Headline status changed (mode, totals, data-plan, counts).</summary>
public sealed class StatusChangedEvent : IpcMessage
{
    public EngineStatus Status { get; set; } = new();
}
