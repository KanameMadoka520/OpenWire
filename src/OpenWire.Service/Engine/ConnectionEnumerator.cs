using System.Net;
using OpenWire.Core.Models;
using OpenWire.Service.Native;

namespace OpenWire.Service.Engine;

/// <summary>
/// Snapshots the live TCP/UDP connection table (IPv4 + IPv6) and attributes each
/// endpoint to its owning application. Geo/host enrichment is applied by the engine.
/// </summary>
public sealed class ConnectionEnumerator
{
    private readonly ProcessResolver _processes;

    public ConnectionEnumerator(ProcessResolver processes) => _processes = processes;

    public List<ConnectionInfo> Snapshot()
    {
        var endpoints = IpHlpApiInterop.GetAllEndpoints();
        var list = new List<ConnectionInfo>(endpoints.Count);

        foreach (var ep in endpoints)
        {
            var app = _processes.Resolve(ep.ProcessId);
            list.Add(new ConnectionInfo
            {
                Protocol = ep.IsTcp ? TransportProtocol.Tcp : TransportProtocol.Udp,
                LocalAddress = ep.LocalAddress.ToString(),
                LocalPort = ep.LocalPort,
                RemoteAddress = ep.RemoteAddress.ToString(),
                RemotePort = ep.RemotePort,
                State = ep.IsTcp ? IpHlpApiInterop.TcpStateName(ep.State) : "-",
                ProcessId = ep.ProcessId,
                AppId = app.Id,
                AppName = app.Name,
            });
        }

        return list;
    }

    /// <summary>True for connections with a real, routable remote peer (not listeners).</summary>
    public static bool HasRemotePeer(ConnectionInfo c)
    {
        if (string.IsNullOrEmpty(c.RemoteAddress)) return false;
        if (c.RemotePort == 0) return false;
        return c.RemoteAddress is not ("0.0.0.0" or "::" or "127.0.0.1" or "::1");
    }

    /// <summary>Best-effort test of whether an address is private/loopback/link-local.</summary>
    public static bool IsLocalAddress(string address)
        => !IPAddress.TryParse(address, out var ip) || IsLocalAddress(ip);

    public static bool IsLocalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out int length)) return false;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return length == 4 && IsLocalIpv4(bytes);

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv4MappedToIPv6 && length == 16) return IsLocalIpv4(bytes[12..]);
            return length == 16 && (bytes[0] & 0xFE) == 0xFC; // unique local fc00::/7
        }

        return false;
    }

    private static bool IsLocalIpv4(ReadOnlySpan<byte> bytes)
        => bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)   // link-local
            || bytes[0] == 0;
}
