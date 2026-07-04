using System.Collections.Concurrent;
using System.Runtime.Versioning;
using OpenWire.Core.Ipc;
using OpenWire.Core.Models;
using OpenWire.Core.Util;
using OpenWire.Service.Firewall;
using OpenWire.Service.Storage;

namespace OpenWire.Service.Engine;

/// <summary>
/// The OpenWire monitoring engine: composition root that owns capture, enrichment,
/// firewall control, alerting, device scanning and persistence, driven by a 1 Hz tick.
/// The IPC server calls its query/command methods and forwards its <see cref="Events"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorEngine : IAsyncDisposable
{
    private const int RingCapacitySeconds = 3 * 60 * 60; // 3h of 1s samples

    private readonly ProcessResolver _processes = new();
    private readonly ConnectionEnumerator _connections;
    private readonly EtwNetworkMonitor _etw = new();
    private readonly InterfaceTrafficMonitor _iface = new();
    private readonly GeoIpResolver _geo;
    private readonly DnsResolver _dns = new();
    private readonly DeviceScanner _scanner;
    private readonly FirewallManager _firewall = new();
    private readonly AlertEngine _alerts = new();
    private readonly HistoryStore _store;

    private readonly object _ringLock = new();
    private readonly Queue<TrafficSample> _ring = new();
    private readonly ConcurrentDictionary<string, AppUsage> _appStore = new();
    private readonly ConcurrentDictionary<string, AppFirewallRule> _firewallCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AppInfo> _pendingApps = new();

    private (long In, long Out) _prevGlobal;
    private bool _haveBaseline;
    private Dictionary<int, (long In, long Out)> _prevPerPid = new();
    private Dictionary<string, (long In, long Out)> _prevEndpoints = new();

    private long _pendingGlobalIn, _pendingGlobalOut;
    private readonly Dictionary<string, (long In, long Out)> _pendingApp = new();
    private readonly Dictionary<string, (long In, long Out)> _pendingHost = new();
    private long _currentBucket;

    private List<ConnectionInfo> _connectionsSnapshot = new();
    private volatile EngineStatus _status = new();
    private AppSettings _settings = new();
    private DateTimeOffset _monitoringSince;
    private DateTimeOffset _lastDeviceScan = DateTimeOffset.MinValue;
    private bool _etwActive;
    private long _tick;
    private int _scanning;

    public event Action<IpcMessage>? Events;

    public string EngineVersion => "0.1.0";
    public bool CanEnforceFirewall { get; private set; }
    public bool GeoIpAvailable => _geo.Available;

    public MonitorEngine(string dataDir)
    {
        _store = new HistoryStore(Path.Combine(dataDir, "openwire.db"));
        _geo = new GeoIpResolver(Path.Combine(dataDir, "GeoLite2-Country.mmdb"));
        _connections = new ConnectionEnumerator(_processes);
        _scanner = new DeviceScanner(new OuiDatabase(Path.Combine(dataDir, "manuf.txt")));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _settings = _store.LoadSettings();
        _dns.Enabled = _settings.ResolveHostNames;
        _monitoringSince = DateTimeOffset.UtcNow;
        _currentBucket = MinuteBucket(_monitoringSince);

        _alerts.SeedKnownApps(_store.GetKnownAppIds());
        _alerts.SeedKnownDevices(_store.GetKnownDeviceIds());
        _alerts.SeedDns(_scanner.GetLocalInfo().DnsServers);

        CanEnforceFirewall = _firewall.CanEnforce();
        _etwActive = _etw.TryStart();
        RefreshFirewallCache();

        Console.WriteLine($"[Engine] started. ETW={_etwActive}, firewall={CanEnforceFirewall}, geoip={_geo.Available}");

        _ = Task.Run(() => TickLoopAsync(ct), ct);
        _ = Task.Run(() => InitialScanAsync(ct), ct);
        await Task.CompletedTask;
    }

    private async Task InitialScanAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            await RescanDevicesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    // ---------------- tick loop ----------------

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Engine] tick error: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        _tick++;
        var now = DateTimeOffset.UtcNow;

        // 1) global throughput delta
        var cur = _etwActive && _etw.IsRunning ? _etw.ReadGlobal() : _iface.ReadGlobal();
        long dIn = 0, dOut = 0;
        if (_haveBaseline)
        {
            dIn = Math.Max(0, cur.In - _prevGlobal.In);
            dOut = Math.Max(0, cur.Out - _prevGlobal.Out);
        }
        _prevGlobal = cur;
        _haveBaseline = true;

        var sample = new TrafficSample(now, dIn, dOut);
        lock (_ringLock)
        {
            _ring.Enqueue(sample);
            while (_ring.Count > RingCapacitySeconds) _ring.Dequeue();
        }
        _pendingGlobalIn += dIn;
        _pendingGlobalOut += dOut;

        // 2) per-process attribution (ETW only)
        if (_etwActive && _etw.IsRunning)
        {
            AttributePerApp(now);
            AttributePerHost();
        }

        // 3) refresh live connection table every 3s
        if (_tick % 3 == 0) RefreshConnections();

        // 4) minute rollup flush
        long bucket = MinuteBucket(now);
        if (bucket != _currentBucket)
        {
            FlushMinute(_currentBucket);
            _currentBucket = bucket;
        }

        // 5) periodic security checks
        if (_tick % 30 == 0) RunPeriodicChecks();

        // 6) scheduled device rescan
        if (now - _lastDeviceScan > TimeSpan.FromMinutes(30))
            _ = RescanDevicesAsync(CancellationToken.None);

        // 7) rebuild status + broadcast tick
        _status = BuildStatus(now, dOut, dIn);
        Events?.Invoke(new LiveTickEvent
        {
            Sample = sample,
            DownloadBytesPerSec = dIn,
            UploadBytesPerSec = dOut,
            ActiveConnectionCount = _connectionsSnapshot.Count,
            ActiveAppCount = _status.ActiveAppCount,
        });
        if (_tick % 5 == 0)
            Events?.Invoke(new StatusChangedEvent { Status = _status });
    }

    private void AttributePerApp(DateTimeOffset now)
    {
        var snap = _etw.SnapshotPerPid();
        foreach (var (pid, val) in snap)
        {
            _prevPerPid.TryGetValue(pid, out var prev);
            long di = Math.Max(0, val.In - prev.In);
            long dono = Math.Max(0, val.Out - prev.Out);
            if (di == 0 && dono == 0) continue;

            var app = _processes.Resolve(pid);
            if (app.Id is "system") continue;

            var usage = GetOrCreateApp(app, now);
            usage.BytesIn += di;
            usage.BytesOut += dono;
            usage.LastSeen = now;
            usage.IsActive = true;

            Accumulate(_pendingApp, app.Id, di, dono);
        }
        _prevPerPid = snap;

        // Decay the "active" flag for apps that didn't move this tick.
        foreach (var u in _appStore.Values)
            if (u.LastSeen < now - TimeSpan.FromSeconds(3)) u.IsActive = false;
    }

    private void AttributePerHost()
    {
        var eps = _etw.SnapshotEndpoints();
        var next = new Dictionary<string, (long, long)>(eps.Count);
        foreach (var (pid, remote, inB, outB) in eps)
        {
            string key = $"{pid}|{remote}";
            next[key] = (inB, outB);
            _prevEndpoints.TryGetValue(key, out var prev);
            long di = Math.Max(0, inB - prev.Item1);
            long dono = Math.Max(0, outB - prev.Item2);
            if (di == 0 && dono == 0) continue;
            if (ConnectionEnumerator.IsLocalAddress(remote)) continue;

            Accumulate(_pendingHost, remote, di, dono);
        }
        _prevEndpoints = next;
    }

    private void FlushMinute(long bucket)
    {
        if (_pendingGlobalIn > 0 || _pendingGlobalOut > 0)
            _store.AddGlobalMinute(bucket, _pendingGlobalIn, _pendingGlobalOut);
        _pendingGlobalIn = _pendingGlobalOut = 0;

        foreach (var (appId, v) in _pendingApp)
            _store.AddAppMinute(appId, bucket, v.In, v.Out);
        _pendingApp.Clear();

        foreach (var (ip, v) in _pendingHost)
        {
            _store.AddHostMinute(ip, bucket, v.In, v.Out);
            var geo = _settings.ResolveGeoIp ? _geo.Resolve(ip) : GeoInfo.Unknown;
            var host = _dns.Resolve(ip);
            _store.UpsertHostMeta(ip, ip, host, geo);
        }
        _pendingHost.Clear();
    }

    private void RefreshConnections()
    {
        try
        {
            var list = _connections.Snapshot();
            foreach (var c in list.Where(ConnectionEnumerator.HasRemotePeer))
            {
                if (_settings.ResolveGeoIp) c.Geo = _geo.Resolve(c.RemoteAddress);
                if (_settings.ResolveHostNames) c.RemoteHost = _dns.Resolve(c.RemoteAddress);
            }
            _connectionsSnapshot = list;

            // Ensure every network-active app appears (even without ETW byte
            // attribution) and refresh its live connection count.
            var now = DateTimeOffset.UtcNow;
            var activeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in list.Where(ConnectionEnumerator.HasRemotePeer).GroupBy(c => c.AppId))
            {
                if (string.IsNullOrEmpty(grp.Key) || grp.Key is "system") continue;
                activeCounts[grp.Key] = grp.Count();
                if (!_appStore.ContainsKey(grp.Key))
                {
                    var app = _processes.Resolve(grp.First().ProcessId);
                    if (app.Id is not ("system" or "unknown"))
                    {
                        var u = GetOrCreateApp(app, now);
                        u.LastSeen = now;
                    }
                }
            }
            foreach (var u in _appStore.Values)
                u.ActiveConnections = activeCounts.TryGetValue(u.App.Id, out var n) ? n : 0;

            if (_settings.MonitorRdp)
                foreach (var a in _alerts.CheckRdp(list)) Persist(a);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Engine] connection refresh: {ex.Message}");
        }
    }

    private void RunPeriodicChecks()
    {
        if (_settings.MonitorDnsChanges)
        {
            var a = _alerts.CheckDnsServers(_scanner.GetLocalInfo().DnsServers);
            if (a is not null) Persist(a);
        }

        if (_settings.DataPlan.Enabled)
        {
            _settings.DataPlan.UsedBytes = _store.DataPlanUsed(CycleStartBucket());
            var a = _alerts.CheckDataPlan(_settings.DataPlan);
            if (a is not null) Persist(a);
        }

        RefreshFirewallCache();
    }

    // ---------------- alerts / firewall ----------------

    private AppUsage GetOrCreateApp(AppInfo app, DateTimeOffset now)
        => _appStore.GetOrAdd(app.Id, _ =>
        {
            _store.UpsertAppMeta(app);
            RaiseNewApp(app);
            return new AppUsage { App = app, FirstSeen = now };
        });

    private void RaiseNewApp(AppInfo app)
    {
        if (!_settings.IsAlertEnabled(AlertKind.NewApp)) return;
        var alert = _alerts.CheckNewApp(app);
        if (alert is null) return;
        Persist(alert);

        if (_settings.FirewallMode == FirewallMode.AskToConnect && CanEnforceFirewall && app.ExecutablePath.Length > 0)
        {
            try
            {
                _firewall.SetAppBlocked(app.ExecutablePath, app.Id, app.Name, blockIn: true, blockOut: true);
                _pendingApps[app.Id] = app;
                RefreshFirewallCache();
                Events?.Invoke(new FirewallPromptEvent { App = app });
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Engine] ask-to-connect block failed: {ex.Message}"); }
        }
    }

    private void Persist(Alert alert)
    {
        _store.InsertAlert(alert);
        Events?.Invoke(new AlertRaisedEvent { Alert = alert });
    }

    private void RefreshFirewallCache()
    {
        try
        {
            var rules = _firewall.GetAppRules();
            _firewallCache.Clear();
            foreach (var r in rules) _firewallCache[r.AppId] = r;
        }
        catch { /* firewall unavailable */ }
    }

    // ---------------- status ----------------

    private EngineStatus BuildStatus(DateTimeOffset now, long upBps, long downBps)
    {
        int active = _appStore.Values.Count(u => u.IsActive);
        var (totIn, totOut) = _store.TotalTraffic();
        var plan = _settings.DataPlan;
        if (plan.Enabled) plan.UsedBytes = _store.DataPlanUsed(CycleStartBucket());

        return new EngineStatus
        {
            MachineName = Environment.MachineName,
            EngineVersion = EngineVersion,
            FirewallMode = _settings.FirewallMode,
            DownloadBytesPerSec = downBps,
            UploadBytesPerSec = upBps,
            TotalBytesIn = totIn + _pendingGlobalIn,
            TotalBytesOut = totOut + _pendingGlobalOut,
            ActiveAppCount = active,
            ActiveConnectionCount = _connectionsSnapshot.Count(ConnectionEnumerator.HasRemotePeer),
            OnlineDeviceCount = _status.OnlineDeviceCount,
            UnreadAlertCount = _store.UnreadAlertCount(),
            MonitoringSince = _monitoringSince,
            DataPlan = plan,
            CanEnforceFirewall = CanEnforceFirewall,
        };
    }

    // ---------------- query API (called by IPC) ----------------

    public EngineStatus GetStatus() => _status;

    public TrafficSeries GetGraph(GraphRange range)
    {
        int interval = range.IntervalSeconds();
        long toSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long fromSec = toSec - (long)range.Duration().TotalSeconds;

        IEnumerable<(long Sec, long In, long Out)> points;
        if (range is GraphRange.FiveMinutes or GraphRange.ThreeHours)
        {
            TrafficSample[] snap;
            lock (_ringLock) snap = _ring.ToArray();
            points = snap.Where(s => s.Time.ToUnixTimeSeconds() >= fromSec)
                         .Select(s => (s.Time.ToUnixTimeSeconds(), s.BytesIn, s.BytesOut));
        }
        else
        {
            var rows = _store.QueryGlobal(fromSec / 60 * 60, toSec / 60 * 60);
            points = rows.Select(r => (r.Bucket, r.In, r.Out));
        }

        return BuildSeries(range, interval, fromSec, toSec, points);
    }

    private static TrafficSeries BuildSeries(GraphRange range, int interval, long fromSec, long toSec,
        IEnumerable<(long Sec, long In, long Out)> points)
    {
        var buckets = new Dictionary<long, (long In, long Out)>();
        foreach (var p in points)
        {
            long b = p.Sec / interval * interval;
            buckets.TryGetValue(b, out var cur);
            buckets[b] = (cur.In + p.In, cur.Out + p.Out);
        }

        var series = new TrafficSeries { Range = range, IntervalSeconds = interval };
        long start = fromSec / interval * interval;
        long end = toSec / interval * interval;
        long peak = 0;
        for (long b = start; b <= end; b += interval)
        {
            buckets.TryGetValue(b, out var v);
            series.Samples.Add(new TrafficSample(DateTimeOffset.FromUnixTimeSeconds(b), v.In, v.Out));
            peak = Math.Max(peak, v.In + v.Out);
        }
        series.PeakBytes = peak;
        return series;
    }

    public UsageResponse GetUsage(GraphRange range, UsageGroupBy groupBy)
    {
        long toSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long fromBucket = (toSec - (long)range.Duration().TotalSeconds) / 60 * 60;
        long toBucket = toSec / 60 * 60;

        var resp = new UsageResponse { GroupBy = groupBy, Range = range };

        if (groupBy == UsageGroupBy.Hosts)
        {
            resp.Hosts = _store.QueryUsageByHost(fromBucket, toBucket);
        }
        else if (groupBy == UsageGroupBy.TrafficType)
        {
            resp.Types = BuildTrafficTypes(_store.QueryUsageByHost(fromBucket, toBucket));
        }
        else
        {
            var apps = _store.QueryUsageByApp(fromBucket, toBucket);
            var byId = apps.ToDictionary(a => a.App.Id, StringComparer.OrdinalIgnoreCase);

            // Overlay live session state (active flag, connections, firewall status).
            foreach (var live in _appStore.Values)
            {
                if (!byId.TryGetValue(live.App.Id, out var a))
                {
                    a = new AppUsage { App = live.App, BytesIn = live.BytesIn, BytesOut = live.BytesOut };
                    byId[live.App.Id] = a;
                    apps.Add(a);
                }
                a.IsActive = live.IsActive;
                a.ActiveConnections = live.ActiveConnections;
                a.FirstSeen = live.FirstSeen;
                a.LastSeen = live.LastSeen;
            }
            foreach (var a in apps)
                a.FirewallStatus = FirewallStatusFor(a.App.Id);

            resp.Apps = apps.OrderByDescending(a => a.Total).ToList();
        }

        resp.TotalBytesIn = resp.Apps.Sum(a => a.BytesIn) + resp.Hosts.Sum(h => h.BytesIn);
        resp.TotalBytesOut = resp.Apps.Sum(a => a.BytesOut) + resp.Hosts.Sum(h => h.BytesOut);
        return resp;
    }

    private List<TrafficTypeUsage> BuildTrafficTypes(List<HostUsage> hosts)
    {
        // Byte-accurate per-protocol classification needs per-port accounting (a later
        // enhancement). For now we present a live port-class split from the current
        // connection table as a representative breakdown, plus the aggregate volume.
        long totalIn = hosts.Sum(h => h.BytesIn);
        long totalOut = hosts.Sum(h => h.BytesOut);
        long total = totalIn + totalOut;

        var classes = _connectionsSnapshot
            .Where(ConnectionEnumerator.HasRemotePeer)
            .GroupBy(c => ClassifyPort(c.RemotePort))
            .ToDictionary(g => g.Key, g => g.Count());
        int conns = Math.Max(1, classes.Values.Sum());

        var result = classes
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new TrafficTypeUsage
            {
                TypeName = kv.Key,
                BytesIn = (long)(totalIn * ((double)kv.Value / conns)),
                BytesOut = (long)(totalOut * ((double)kv.Value / conns)),
            })
            .ToList();

        if (result.Count == 0)
            result.Add(new TrafficTypeUsage { TypeName = "Internet", BytesIn = totalIn, BytesOut = totalOut });

        foreach (var t in result) t.Fraction = total > 0 ? (double)t.Total / total : 0;
        return result;
    }

    private static string ClassifyPort(int port) => port switch
    {
        443 or 8443 => "Web (HTTPS)",
        80 or 8080 => "Web (HTTP)",
        53 => "DNS",
        20 or 21 => "FTP",
        22 => "SSH",
        25 or 465 or 587 => "Email (SMTP)",
        110 or 143 or 993 or 995 => "Email",
        3389 => "Remote Desktop",
        123 => "Time (NTP)",
        _ => "Other",
    };

    public List<ConnectionInfo> GetConnections() =>
        _connectionsSnapshot.Where(ConnectionEnumerator.HasRemotePeer).ToList();

    public (FirewallStatus Status, List<AppFirewallRule> Rules, List<FirewallProfile> Profiles) GetFirewall()
    {
        var status = new FirewallStatus
        {
            Mode = _settings.FirewallMode,
            ActiveProfile = "Default",
            BlockedAppCount = _firewallCache.Count,
            PendingAppCount = _pendingApps.Count,
            CanEnforce = CanEnforceFirewall,
        };
        var profiles = new List<FirewallProfile>
        {
            new() { Name = "Default", Mode = _settings.FirewallMode, IsActive = true },
        };
        return (status, _firewallCache.Values.ToList(), profiles);
    }

    private AppFirewallStatus FirewallStatusFor(string appId)
    {
        if (_pendingApps.ContainsKey(appId)) return AppFirewallStatus.Pending;
        if (_firewallCache.ContainsKey(appId)) return AppFirewallStatus.Blocked;
        return AppFirewallStatus.Allowed;
    }

    public void SetFirewallMode(FirewallMode mode)
    {
        var old = _settings.FirewallMode;
        _settings.FirewallMode = mode;
        _store.SaveSettings(_settings);

        if (CanEnforceFirewall)
        {
            if (mode == FirewallMode.Off && old == FirewallMode.Off) { /* no-op */ }
            if (old != mode)
            {
                if (mode == FirewallMode.Off) _firewall.SetBlockAll(false);
            }
        }
        _status = BuildStatus(DateTimeOffset.UtcNow, 0, 0);
        Events?.Invoke(new StatusChangedEvent { Status = _status });
    }

    public void SetAppBlocked(string appId, string exePath, bool blockIn, bool blockOut)
    {
        if (!CanEnforceFirewall) return;
        if (string.IsNullOrEmpty(exePath) && _appStore.TryGetValue(appId, out var u)) exePath = u.App.ExecutablePath;
        string name = _appStore.TryGetValue(appId, out var au) ? au.App.Name : Path.GetFileNameWithoutExtension(exePath);

        if (!blockIn && !blockOut) _firewall.UnblockApp(appId);
        else _firewall.SetAppBlocked(exePath, appId, name, blockIn, blockOut);

        _pendingApps.TryRemove(appId, out _);
        RefreshFirewallCache();
    }

    public void ResolveAppDecision(string appId, bool allow)
    {
        if (!_pendingApps.TryRemove(appId, out var app)) return;
        if (allow) _firewall.UnblockApp(appId);
        // if !allow the block rules stay in place
        RefreshFirewallCache();
    }

    public List<Alert> GetAlerts(int limit) => _store.GetAlerts(limit);

    public void AckAlert(long id, bool all)
    {
        if (all) _store.AckAllAlerts();
        else _store.AckAlert(id);
    }

    public List<Device> GetDevices() => _store.GetDevices();

    public async Task RescanDevicesAsync(CancellationToken ct)
    {
        // Guard against overlapping scans (multiple clients, scheduled + manual).
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            _lastDeviceScan = DateTimeOffset.UtcNow;
            List<Device> found;
            try { found = await _scanner.ScanAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[Engine] device scan: {ex.Message}"); return; }

            foreach (var d in found) _store.UpsertDevice(d);
            _store.MarkDevicesOffline(found.Select(d => d.Id));

            if (_settings.MonitorNewDevices)
                foreach (var a in _alerts.CheckDevices(found))
                    Persist(a);

            foreach (var d in found)
                Events?.Invoke(new DeviceChangedEvent { Device = d });

            var status = _status;
            status.OnlineDeviceCount = found.Count(d => d.IsOnline);
            _status = status;
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    public void RenameDevice(string id, string name) => _store.RenameDevice(id, name);
    public void ForgetDevice(string id) => _store.ForgetDevice(id);

    public AppSettings GetSettings() => _settings;

    public void SetSettings(AppSettings settings)
    {
        _settings = settings;
        _dns.Enabled = settings.ResolveHostNames;
        _store.SaveSettings(settings);
        SetFirewallMode(settings.FirewallMode);
    }

    // ---------------- helpers ----------------

    private static long MinuteBucket(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60 * 60;

    private long CycleStartBucket()
    {
        var now = DateTimeOffset.UtcNow;
        int day = Math.Clamp(_settings.DataPlan.BillingCycleStartDay, 1, 28);
        var start = new DateTimeOffset(now.Year, now.Month, day, 0, 0, 0, TimeSpan.Zero);
        if (start > now) start = start.AddMonths(-1);
        return MinuteBucket(start);
    }

    private static void Accumulate(Dictionary<string, (long In, long Out)> map, string key, long inB, long outB)
    {
        map.TryGetValue(key, out var cur);
        map[key] = (cur.In + inB, cur.Out + outB);
    }

    public async ValueTask DisposeAsync()
    {
        try { FlushMinute(_currentBucket); } catch { }
        _etw.Dispose();
        _geo.Dispose();
        _store.Dispose();
        await Task.CompletedTask;
    }
}
