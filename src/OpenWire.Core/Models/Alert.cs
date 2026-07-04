namespace OpenWire.Core.Models;

/// <summary>A single alert raised by the engine and shown on the Alerts screen.</summary>
public sealed class Alert
{
    public long Id { get; set; }

    public DateTimeOffset Time { get; set; }

    public AlertKind Kind { get; set; }

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    /// <summary>Short headline, e.g. "New app connected to the network".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Full descriptive text.</summary>
    public string Message { get; set; } = string.Empty;

    // Optional associations for drill-down / iconography.
    public string? AppId { get; set; }
    public string? AppName { get; set; }
    public string? AppIconPngBase64 { get; set; }
    public string? DeviceId { get; set; }
    public string? RemoteHost { get; set; }

    public bool Acknowledged { get; set; }
}
