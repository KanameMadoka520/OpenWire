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

    /// <summary>New devices appearing on the LAN since the last scan.</summary>
    public List<Alert> CheckDevices(IEnumerable<Device> current)
    {
        var alerts = new List<Alert>();
        foreach (var d in current)
        {
            if (d.IsThisDevice) { _knownDevices.Add(d.Id); continue; }
            if (string.IsNullOrEmpty(d.Id)) continue;
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
