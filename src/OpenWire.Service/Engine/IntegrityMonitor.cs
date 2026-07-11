using Microsoft.Win32;
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
    private string? _proxyState;
    private bool _proxySeeded;
    private string? _netState;
    private bool _netSeeded;

    /// <summary>Capture the current state as the baseline (no alerts on first run).</summary>
    public void Seed()
    {
        var h = HashHostsFile();
        if (h is not null) { _hostsHash = h; _hostsSeeded = true; }
        foreach (var (ip, mac) in CurrentGateways()) _gatewayMac[ip] = mac;
        var p = ReadProxyState();
        if (p is not null) { _proxyState = p; _proxySeeded = true; }
        var n = NetworkListManagerInterop.ReadState();
        if (n is not null) { _netState = n; _netSeeded = true; }
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

    /// <summary>The system internet proxy configuration (WinINET) changed. A silently-injected proxy
    /// is a classic traffic-redirection vector, so a change is surfaced as a warning.</summary>
    public Alert? CheckProxy()
    {
        string? now = ReadProxyState();
        if (now is null) return null;                    // unreadable — no observation, no alarm
        if (!_proxySeeded) { _proxyState = now; _proxySeeded = true; return null; }
        if (now == _proxyState) return null;
        _proxyState = now;
        return new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.ProxySettingsChanged,
            Severity = AlertSeverity.Warning,
            Title = "Proxy settings changed",
            Message = $"Your system internet proxy configuration changed ({DescribeProxy(now)}). If you did not change it, review your proxy settings — malware can silently redirect traffic this way.",
        };
    }

    /// <summary>Internet reachability was lost or restored since the last observation. Only the
    /// up↔down transition of overall internet access is reported (minor connectivity-flag jitter is
    /// ignored) so the alert fires on the event that actually matters.</summary>
    public Alert? CheckInternetAccess()
    {
        string? now = NetworkListManagerInterop.ReadState();
        if (now is null) return null;                    // NLM unqueryable — no observation, no alarm
        if (!_netSeeded) { _netState = now; _netSeeded = true; return null; }
        string prev = _netState!;
        _netState = now;

        bool wasUp = NetworkListManagerInterop.IsInternetUp(prev);
        bool isUp = NetworkListManagerInterop.IsInternetUp(now);
        if (wasUp == isUp) return null;                  // connectivity changed but internet up/down did not

        return new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.InternetAccessChanged,
            Severity = isUp ? AlertSeverity.Info : AlertSeverity.Warning,
            Title = isUp ? "Internet access restored" : "Internet access lost",
            Message = isUp
                ? "This computer regained internet access."
                : "This computer lost internet access. If you did not expect this, check your connection — a dropped link, a captive portal, or a security tool may have cut it.",
        };
    }

    /// <summary>Canonical WinINET proxy state string (enable|server|pac), or null if unreadable.</summary>
    private static string? ReadProxyState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return "0||";
            int enabled = (key.GetValue("ProxyEnable") as int?) ?? 0;
            string server = key.GetValue("ProxyServer") as string ?? "";
            string pac = key.GetValue("AutoConfigURL") as string ?? "";
            return $"{enabled}|{server}|{pac}";
        }
        catch { return null; }
    }

    /// <summary>Human-readable summary of a proxy state string for the alert message.</summary>
    private static string DescribeProxy(string state)
    {
        var parts = state.Split('|');
        string enabled = parts.Length > 0 ? parts[0] : "0";
        string server = parts.Length > 1 ? parts[1] : "";
        string pac = parts.Length > 2 ? parts[2] : "";
        if (enabled == "1" && server.Length > 0) return $"now using proxy {server}";
        if (!string.IsNullOrEmpty(pac)) return "now using an auto-config script";
        return "proxy now disabled (direct connection)";
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
