namespace OpenWire.Core.Models;

/// <summary>A device discovered on the local network (the "Things" screen).</summary>
public sealed class Device
{
    /// <summary>Stable identifier: MAC address when known, else IP.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-assigned or auto-detected friendly name.</summary>
    public string Name { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Hardware vendor resolved from the MAC OUI prefix.</summary>
    public string Vendor { get; set; } = string.Empty;

    public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

    public bool IsOnline { get; set; }
    public bool IsGateway { get; set; }
    public bool IsThisDevice { get; set; }

    /// <summary>True until the user has acknowledged this device (drives "new device" alerts).</summary>
    public bool IsNew { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
