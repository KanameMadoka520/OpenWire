using System.Net;
using System.Net.NetworkInformation;
using OpenWire.Core.Models;
using OpenWire.Service.Native;

namespace OpenWire.Service.Engine;

/// <summary>Own-machine network facts for the Things "network info" panel.</summary>
public sealed record LocalNetworkInfo(
    List<string> LocalAddresses,
    string MacAddress,
    List<string> Gateways,
    List<string> DnsServers,
    string MachineName);

/// <summary>
/// Discovers devices on the local subnet: ping-sweeps the /24, reads the ARP table
/// for IP↔MAC pairs, resolves vendors from the OUI, and classifies device kinds.
/// </summary>
public sealed class DeviceScanner
{
    private readonly OuiDatabase _oui;

    public DeviceScanner(OuiDatabase oui) => _oui = oui;

    public LocalNetworkInfo GetLocalInfo()
    {
        var addrs = new List<string>();
        var gateways = new List<string>();
        var dns = new List<string>();
        string mac = string.Empty;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = ni.GetIPProperties();
            foreach (var u in props.UnicastAddresses)
                if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    addrs.Add(u.Address.ToString());
            foreach (var g in props.GatewayAddresses)
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    gateways.Add(g.Address.ToString());
            foreach (var d in props.DnsAddresses)
                dns.Add(d.ToString());

            if (mac.Length == 0)
            {
                var bytes = ni.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6)
                    mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
        }

        return new LocalNetworkInfo(
            addrs.Distinct().ToList(), mac,
            gateways.Distinct().ToList(), dns.Distinct().ToList(),
            Environment.MachineName);
    }

    public async Task<List<Device>> ScanAsync(CancellationToken ct)
    {
        var gateways = new HashSet<string>();
        var selfIps = new HashSet<string>();
        string selfMac = string.Empty;

        // Collect local facts and the set of /24 targets to sweep.
        var targets = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = ni.GetIPProperties();
            foreach (var g in props.GatewayAddresses)
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    gateways.Add(g.Address.ToString());

            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                selfIps.Add(u.Address.ToString());
                if (selfMac.Length == 0)
                {
                    var b = ni.GetPhysicalAddress().GetAddressBytes();
                    if (b.Length == 6) selfMac = string.Join(":", b.Select(x => x.ToString("X2")));
                }

                var octets = u.Address.GetAddressBytes();
                for (int host = 1; host <= 254; host++)
                    targets.Add($"{octets[0]}.{octets[1]}.{octets[2]}.{host}");
            }
        }

        await PingSweepAsync(targets, ct).ConfigureAwait(false);

        // Read the freshly-populated ARP table and de-duplicate by MAC.
        var now = DateTimeOffset.UtcNow;
        var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArpInterop.ArpEntry>();
        foreach (var entry in ArpInterop.GetArpTable())
        {
            string ip = entry.Address.ToString();
            if (ip == "0.0.0.0" || ip.EndsWith(".255")) continue;
            if (!seenMacs.Add(entry.Mac)) continue;
            entries.Add(entry);
        }

        // Resolve host names for all devices concurrently (each has its own timeout).
        var hosts = await Task.WhenAll(entries.Select(e => TryResolveHostAsync(e.Address.ToString(), ct))).ConfigureAwait(false);

        var devices = new List<Device>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string ip = entry.Address.ToString();
            string host = hosts[i];
            string vendor = _oui.Lookup(entry.Mac);
            bool isGateway = gateways.Contains(ip);

            var kind = isGateway ? DeviceKind.Router : _oui.GuessKind(vendor, host);
            devices.Add(new Device
            {
                Id = entry.Mac,
                Name = host.Length > 0 ? host : (vendor.Length > 0 ? vendor : "Unknown device"),
                IpAddress = ip,
                MacAddress = entry.Mac,
                Vendor = vendor,
                Kind = kind,
                IsOnline = true,
                IsGateway = isGateway,
                IsThisDevice = false,
                FirstSeen = now,
                LastSeen = now,
            });
        }

        // Always include this machine.
        string selfIp = selfIps.FirstOrDefault(i => !ConnectionEnumerator.IsLocalAddress(i)) ?? selfIps.FirstOrDefault() ?? string.Empty;
        devices.Insert(0, new Device
        {
            Id = selfMac.Length > 0 ? selfMac : "this-device",
            Name = Environment.MachineName,
            IpAddress = selfIp,
            MacAddress = selfMac,
            Vendor = _oui.Lookup(selfMac),
            Kind = DeviceKind.ThisComputer,
            IsOnline = true,
            IsThisDevice = true,
            FirstSeen = now,
            LastSeen = now,
        });

        return devices;
    }

    private static async Task PingSweepAsync(HashSet<string> targets, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(128);
        var tasks = new List<Task>(targets.Count);

        foreach (var target in targets)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    await ping.SendPingAsync(target, 300).ConfigureAwait(false);
                }
                catch { /* unreachable */ }
                finally { gate.Release(); }
            }, ct));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* individual failures already swallowed */ }
    }

    private static async Task<string> TryResolveHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            var he = await Dns.GetHostEntryAsync(ip, cts.Token).ConfigureAwait(false);
            return string.Equals(he.HostName, ip, StringComparison.Ordinal) ? string.Empty : he.HostName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
