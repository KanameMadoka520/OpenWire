using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.Service.Engine;

/// <summary>
/// Stateful detector that turns observed changes (a new app touching the network,
/// a device joining the LAN, DNS servers changing, an inbound RDP session, a data
/// cap crossed) into <see cref="Alert"/>s. It only detects; the engine decides
/// which monitors are enabled and persists/broadcasts the results.
/// </summary>
public sealed class AlertEngine
{
    private readonly HashSet<string> _knownApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _onlineDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _onlineSeeded;
    private readonly HashSet<string> _activeRdp = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _lastDns;
    private bool _dataWarned;

    public void SeedKnownApps(IEnumerable<string> appIds)
    {
        foreach (var id in appIds) _knownApps.Add(id);
    }

    public void SeedKnownDevices(IEnumerable<string> deviceIds)
    {
        foreach (var id in deviceIds) _knownDevices.Add(id);
    }

    public void SeedDns(List<string> dns) => _lastDns = Normalize(dns);

    /// <summary>First network activity from a previously-unseen application.</summary>
    public Alert? CheckNewApp(AppInfo app)
    {
        if (string.IsNullOrEmpty(app.Id)) return null;
        if (app.Id is "system" or "unknown") return null;
        if (!_knownApps.Add(app.Id)) return null;

        return new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.NewApp,
            Severity = AlertSeverity.Info,
            Title = "New app connected to the network",
            Message = $"{app.Name} accessed the network for the first time.",
            AppId = app.Id,
            AppName = app.Name,
        };
    }

    /// <summary>A known application's on-disk metadata changed versus the recorded baseline — a new
    /// version, a different Authenticode signer, or a binary that lost its signature. A silent change
    /// to an already-trusted program is a classic replacement / hijack vector. Losing a signature is
    /// treated as a warning; a routine version bump is informational.</summary>
    public Alert? CheckAppInfo(AppInfo fresh, string prevPublisher, string prevVersion, bool prevSigned)
    {
        string nowPub = fresh.Publisher ?? "";
        string nowVer = fresh.Version ?? "";

        var changes = new List<string>();
        if (!string.Equals(prevVersion, nowVer, StringComparison.Ordinal)
            && !(prevVersion.Length == 0 && nowVer.Length == 0))
            changes.Add($"version {Show(prevVersion)} → {Show(nowVer)}");
        if (!string.Equals(prevPublisher, nowPub, StringComparison.Ordinal)
            && !(prevPublisher.Length == 0 && nowPub.Length == 0))
            changes.Add($"publisher {Show(prevPublisher)} → {Show(nowPub)}");
        if (prevSigned != fresh.IsSigned)
            changes.Add(fresh.IsSigned ? "now digitally signed" : "no longer digitally signed");

        if (changes.Count == 0) return null;

        bool lostSignature = prevSigned && !fresh.IsSigned;
        return new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.AppInfoChanged,
            Severity = lostSignature ? AlertSeverity.Warning : AlertSeverity.Info,
            Title = "Application changed",
            Message = $"{fresh.Name} changed on disk: {string.Join("; ", changes)}. If you did not update this app, review it — a silent change to a trusted program can indicate tampering.",
            AppId = fresh.Id,
            AppName = fresh.Name,
        };
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;

    /// <summary>Devices joining (new) or leaving the LAN since the last full scan. The input is the
    /// set discovered online by the latest scan; anything that was online last time and is now absent
    /// is treated as having left. The first scan only seeds the baseline (no leave alerts).</summary>
    public List<Alert> CheckDevices(IEnumerable<Device> current)
    {
        var alerts = new List<Alert>();
        var nowOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in current)
        {
            if (string.IsNullOrEmpty(d.Id)) continue;
            nowOnline.Add(d.Id);
            _deviceNames[d.Id] = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name;
            if (d.IsThisDevice) { _knownDevices.Add(d.Id); continue; }
            if (!_knownDevices.Add(d.Id)) continue;

            alerts.Add(new Alert
            {
                Time = DateTimeOffset.UtcNow,
                Kind = AlertKind.NewDevice,
                Severity = AlertSeverity.Info,
                Title = "New device on your network",
                Message = $"A new device just joined your network: {d.Name} ({d.IpAddress}, {d.MacAddress}).",
                DeviceId = d.Id,
            });
        }

        // Devices that were online at the previous scan but are gone now have left the network.
        if (_onlineSeeded)
        {
            foreach (var id in _onlineDevices)
            {
                if (nowOnline.Contains(id)) continue;
                string name = _deviceNames.TryGetValue(id, out var n) ? n : id;
                alerts.Add(new Alert
                {
                    Time = DateTimeOffset.UtcNow,
                    Kind = AlertKind.DeviceLeft,
                    Severity = AlertSeverity.Info,
                    Title = "Device left your network",
                    Message = $"A device left your network: {name}.",
                    DeviceId = id,
                });
            }
        }

        _onlineDevices = nowOnline;
        _onlineSeeded = true;
        return alerts;
    }

    /// <summary>The system's configured DNS servers changed.</summary>
    public Alert? CheckDnsServers(List<string> current)
    {
        var norm = Normalize(current);
        if (_lastDns is null)
        {
            _lastDns = norm;
            return null;
        }
        if (_lastDns.SequenceEqual(norm)) return null;

        var alert = new Alert
        {
            Time = DateTimeOffset.UtcNow,
            Kind = AlertKind.DnsServerChanged,
            Severity = AlertSeverity.Warning,
            Title = "DNS server changed",
            Message = $"Your DNS servers changed from [{string.Join(", ", _lastDns)}] to [{string.Join(", ", norm)}].",
        };
        _lastDns = norm;
        return alert;
    }

    /// <summary>A new inbound Remote Desktop session was established.</summary>
    public List<Alert> CheckRdp(IEnumerable<ConnectionInfo> connections)
    {
        var alerts = new List<Alert>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in connections)
        {
            bool inboundRdp = c.Protocol == TransportProtocol.Tcp
                && c.LocalPort == 3389
                && c.State == "Established"
                && ConnectionEnumerator.HasRemotePeer(c);
            if (!inboundRdp) continue;

            string key = c.RemoteAddress;
            live.Add(key);
            if (!_activeRdp.Add(key)) continue;

            alerts.Add(new Alert
            {
                Time = DateTimeOffset.UtcNow,
                Kind = AlertKind.RdpConnection,
                Severity = AlertSeverity.Warning,
                Title = "Remote Desktop connection",
                Message = $"An incoming RDP session was established from {c.RemoteAddress}.",
                RemoteHost = c.RemoteAddress,
            });
        }

        _activeRdp.IntersectWith(live); // forget closed sessions so a reconnect re-alerts
        return alerts;
    }

    /// <summary>The monthly data plan crossed its warning threshold.</summary>
    public Alert? CheckDataPlan(DataPlan plan)
    {
        if (!plan.Enabled || plan.LimitBytes <= 0)
        {
            _dataWarned = false;
            return null;
        }

        if (plan.UsedFraction >= plan.WarnAtFraction)
        {
            if (_dataWarned) return null;
            _dataWarned = true;
            return new Alert
            {
                Time = DateTimeOffset.UtcNow,
                Kind = AlertKind.DataLimitReached,
                Severity = AlertSeverity.Warning,
                Title = "Data plan limit approaching",
                Message = $"You've used {ByteFormatter.Bytes(plan.UsedBytes)} of your {ByteFormatter.Bytes(plan.LimitBytes)} data plan ({plan.UsedFraction:P0}).",
            };
        }

        if (plan.UsedFraction < plan.WarnAtFraction * 0.9)
            _dataWarned = false; // reset with hysteresis (e.g. after a cycle reset)

        return null;
    }

    private static List<string> Normalize(List<string> items)
        => items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().OrderBy(s => s).ToList();
}
