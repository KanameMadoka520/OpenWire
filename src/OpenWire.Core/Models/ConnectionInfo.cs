namespace OpenWire.Core.Models;

/// <summary>A single live network connection owned by a process.</summary>
public sealed class ConnectionInfo
{
    public TransportProtocol Protocol { get; set; }

    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }

    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }

    /// <summary>Resolved reverse-DNS name of the remote endpoint, if known.</summary>
    public string RemoteHost { get; set; } = string.Empty;

    /// <summary>TCP state ("Established", "Listen", ...) or "-" for UDP.</summary>
    public string State { get; set; } = string.Empty;

    public int ProcessId { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;

    public GeoInfo Geo { get; set; } = GeoInfo.Unknown;

    /// <summary>Human-readable remote label: host name if resolved, else IP.</summary>
    public string RemoteDisplay => string.IsNullOrEmpty(RemoteHost) ? RemoteAddress : RemoteHost;
}
