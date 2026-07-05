using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenWire.Core.Models;
using OpenWire.Service.Native;

namespace OpenWire.Service.Engine;

/// <summary>
/// Watches two things that change rarely but matter a lot when they do: the system
/// hosts file (a classic silent domain-redirection vector) and the default gateway's
/// MAC address (a change under a stable gateway IP is a hallmark of ARP spoofing /
/// man-in-the-middle). Both checks are cheap and polled from the engine's periodic
/// security pass. Every read is guarded; an unreadable resource never false-alarms.
/// </summary>
public sealed class IntegrityMonitor
{
    private static readonly string HostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private string? _hostsHash;
    private bool _hostsSeeded;
    private readonly Dictionary<string, string> _gatewayMac = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Capture the current state as the baseline (no alerts on first run).</summary>
    public void Seed()
    {
        var h = HashHostsFile();
        if (h is not null) { _hostsHash = h; _hostsSeeded = true; }
        foreach (var (ip, mac) in CurrentGateways()) _gatewayMac[ip] = mac;
    }

    /// <summary>The hosts file was modified since the last observation.</summary>
    public Alert? CheckHostsFile()
    {
        string? now = HashHostsFile();
        if (now is null) return null;                    // unreadable — no observation, no alarm
        if (!_hostsSeeded) { _hostsHash = now; _hostsSeeded = true; return null; }
        if (now == _hostsHash) return null;
        _hostsHash = now;
        return new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.HostsFileChanged,
            Severity = AlertSeverity.Warning,
            Title = "Hosts file changed",
            Message = $"The system hosts file was modified ({HostsPath}). This file can silently redirect domain names; review it if you did not expect the change.",
        };
    }

    /// <summary>The default gateway's MAC changed while its IP stayed the same (possible ARP spoofing).</summary>
    public List<Alert> CheckGatewayArp()
    {
        var alerts = new List<Alert>();
        foreach (var (ip, mac) in CurrentGateways())
        {
            if (_gatewayMac.TryGetValue(ip, out var prev) &&
                !prev.Equals(mac, StringComparison.OrdinalIgnoreCase))
            {
                alerts.Add(new Alert
                {
                    Time = DateTimeOffset.UtcNow,
                    Kind = AlertKind.ArpSpoofing,
                    Severity = AlertSeverity.Critical,
                    Title = "Gateway MAC address changed",
                    Message = $"The MAC address for gateway {ip} changed from {prev} to {mac}. This can indicate ARP spoofing (a man-in-the-middle on your local network).",
                    RemoteHost = ip,
                });
            }
            _gatewayMac[ip] = mac;
        }
        return alerts;
    }

    private static string? HashHostsFile()
    {
        try
        {
            if (!File.Exists(HostsPath)) return "absent";
            using var fs = File.OpenRead(HostsPath);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { return null; }   // locked/unreadable → no observation
    }

    private static IEnumerable<(string Ip, string Mac)> CurrentGateways()
    {
        var result = new List<(string, string)>();
        try
        {
            var arp = ArpInterop.GetArpTable();
            var arpByIp = arp.GroupBy(e => e.Address.ToString())
                             .ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.OrdinalIgnoreCase);

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                {
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = gw.Address.ToString();
                    if (arpByIp.TryGetValue(ip, out var mac)) result.Add((ip, mac));
                }
            }
        }
        catch { /* best effort */ }
        return result;
    }
}
