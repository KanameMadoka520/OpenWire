using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
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
    private const int SparkHistorySeconds = 60;           // per-app sparkline window

    private readonly ProcessResolver _processes = new();
    private readonly ConnectionEnumerator _connections;
    private readonly EtwNetworkMonitor _etw = new();
    private readonly InterfaceTrafficMonitor _iface = new();
    private readonly HardwareMonitor _hardware = new();
    private readonly GeoIpResolver _geo;
    private readonly DnsResolver _dns = new();
    private readonly DeviceScanner _scanner;
    private readonly FirewallManager _firewall = new();
    private readonly AlertEngine _alerts = new();
    private readonly ReputationService _reputation = new();
    private readonly IntegrityMonitor _integrity = new();
    private readonly HistoryStore _store;

    private readonly object _ringLock = new();
    private readonly object _settingsLock = new();
    private readonly Queue<TrafficSample> _ring = new();
    private readonly ConcurrentDictionary<string, AppUsage> _appStore = new();
    private volatile ConcurrentDictionary<string, AppFirewallRule> _firewallCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AppInfo> _pendingApps = new();

    private (long In, long Out) _prevGlobal;
    private bool _haveBaseline;
    private bool _baselineFromEtw;
    private Task? _tickLoop;
    private CancellationTokenSource? _engineCts;
    private Dictionary<int, (long In, long Out)> _prevPerPid = new();
    private Dictionary<string, (long In, long Out)> _prevEndpoints = new();

    private long _pendingGlobalIn, _pendingGlobalOut;
    private readonly Dictionary<string, (long In, long Out)> _pendingApp = new();
    private readonly Dictionary<string, (long In, long Out)> _pendingHost = new();
    private long _currentBucket;

    private List<ConnectionInfo> _connectionsSnapshot = new();
    private volatile EngineStatus _status = new();
    private AppSettings _settings = new();
    private NetworkInfo _network = NetworkInfo.None;
    private DateTimeOffset _monitoringSince;
    private DateTimeOffset _lastDeviceScan = DateTimeOffset.MinValue;
    private bool _etwActive;
    private long _tick;
    private int _scanning;

    // Usage-anomaly alert de-duplication: raise each distinct anomaly at most once per local day.
    private readonly HashSet<string> _anomaliesAlertedToday = new(StringComparer.OrdinalIgnoreCase);
    private long _anomalyDayStamp = -1;

    public event Action<IpcMessage>? Events;

    public string EngineVersion => "0.1.0";
    public bool CanEnforceFirewall { get; private set; }
    public bool GeoIpAvailable => _geo.Available;

    private readonly GeoIpUpdater _geoUpdater;
    private int _geoUpdating; // 0/1 guard so only one download runs at a time

    private readonly string _dataDir;

    public MonitorEngine(string dataDir)
    {
        _dataDir = dataDir;
        _store = new HistoryStore(Path.Combine(dataDir, "openwire.db"));
        // Priority: an in-app-updated database (writable data dir) wins, then a user-supplied
        // MaxMind GeoLite2, then the DB-IP Lite database bundled beside the engine (CC-BY 4.0,
        // see NOTICE). Keeping the download in the data dir decouples the GeoIP database from the
        // app build, so even an old OpenWire can be kept current.
        _geo = new GeoIpResolver(
            Path.Combine(dataDir, "geoip-country.mmdb"),
            Path.Combine(dataDir, "GeoLite2-Country.mmdb"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "dbip-country-lite.mmdb"));
        _geoUpdater = new GeoIpUpdater(dataDir, _geo);
        _connections = new ConnectionEnumerator(_processes);
        _scanner = new DeviceScanner(new OuiDatabase(Path.Combine(dataDir, "manuf.txt")));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _settings = _store.LoadSettings();
        if (LegacyAutoStartTask.RemoveIfPresent())
        {
            _settings.LaunchOnStartup = true;
            _store.SaveSettings(_settings);
        }
        _dns.Enabled = _settings.ResolveHostNames;
        _reputation.SetApiKey(_settings.VirusTotalApiKey);
        EnsureProfiles();
        _network = NetworkIdentity.Current();
        _monitoringSince = DateTimeOffset.UtcNow;
        _currentBucket = MinuteBucket(_monitoringSince);

        _alerts.SeedKnownApps(_store.GetKnownAppIds());
        _alerts.SeedKnownDevices(_store.GetKnownDeviceIds());
        _alerts.SeedDns(_scanner.GetLocalInfo().DnsServers);
        _integrity.Seed();

        CanEnforceFirewall = _firewall.CanEnforce();
        // Lockdown is a temporary UI-controlled overlay, not a persisted policy. Clear any
        // stale generation left by a crash before the engine begins reporting its state.
        if (CanEnforceFirewall)
            try { _firewall.SetBlockAll(false); } catch { }
        _etwActive = _etw.TryStart();
        _hardware.Start();
        RefreshFirewallCache();
        PruneHistory();

        Console.WriteLine($"[Engine] started. ETW={_etwActive}, firewall={CanEnforceFirewall}, geoip={_geo.Available}");

        // Own cancellation source (linked to the caller's) so DisposeAsync can stop the
        // tick loop and flush safely even when the caller never cancels (e.g. self-test).
        _engineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _engineCts.Token;
        _tickLoop = Task.Run(() => TickLoopAsync(token), token);
        _ = Task.Run(() => InitialScanAsync(token), token);
        if (_settings.GeoIpAutoUpdate) _ = Task.Run(() => AutoUpdateGeoIpAsync(token), token);
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

        // 1) global throughput delta. The counter source can switch between ETW and the
        // interface counters at runtime (the ETW session drops on sleep/resume, buffer
        // loss, or an external tool stopping it). Their absolute scales differ by orders
        // of magnitude, so only diff against a baseline taken from the *same* source —
        // otherwise the first tick after a switch records one enormous false spike.
        bool sourceEtw = _etwActive && _etw.IsRunning;
        var cur = sourceEtw ? _etw.ReadGlobal() : _iface.ReadGlobal();
        long dIn = 0, dOut = 0;
        if (_haveBaseline && sourceEtw == _baselineFromEtw)
        {
            dIn = Math.Max(0, cur.In - _prevGlobal.In);
            dOut = Math.Max(0, cur.Out - _prevGlobal.Out);
        }
        _prevGlobal = cur;
        _haveBaseline = true;
        _baselineFromEtw = sourceEtw;

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

        // 5b) usage-anomaly scan (~10 min) and history pruning (~hourly)
        if (_tick % 600 == 0) RunUsageAnomalyChecks();
        if (_tick % 3600 == 0) PruneHistory();

        // 5c) per-minute: drop caches keyed by dead PIDs (process-churn-heavy
        // machines otherwise grow them forever, and dead flows clog the ETW
        // endpoint cap). ~15 min: compact the LOH — a long-lived service that
        // serializes large IPC responses fragments it without this.
        if (_tick % 60 == 0) PruneProcessCaches();
        if (_tick > 0 && _tick % 900 == 0)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive);
        }

        // 6) scheduled device rescan (only when auto-scan is enabled)
        if (_settings.AutoScanDevices && now - _lastDeviceScan > TimeSpan.FromMinutes(30))
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

        // Rates are per-second deltas; reset before re-accumulating this tick.
        foreach (var u in _appStore.Values) { u.DownRate = 0; u.UpRate = 0; }

        var childMap = new Dictionary<string, List<AppProcess>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pid, val) in snap)
        {
            var app = _processes.Resolve(pid);
            if (app.Id is "system") continue;
            if (val.In + val.Out == 0) continue;

            _prevPerPid.TryGetValue(pid, out var prev);
            long di = Math.Max(0, val.In - prev.In);
            long dono = Math.Max(0, val.Out - prev.Out);

            // A PID with history but no delta must not resurrect an app the idle
            // eviction already dropped (dead PIDs linger in the ETW counters until
            // the per-minute prune) — that would defeat eviction and re-allocate
            // its sparkline every tick forever.
            if (di == 0 && dono == 0 && !_appStore.ContainsKey(app.Id)) continue;

            var usage = GetOrCreateApp(app, now);
            if (di > 0 || dono > 0)
            {
                usage.BytesIn += di;
                usage.BytesOut += dono;
                usage.LastSeen = now;
                usage.IsActive = true;
                usage.DownRate += di;
                usage.UpRate += dono;
                Accumulate(_pendingApp, app.Id, di, dono);
            }

            if (!childMap.TryGetValue(app.Id, out var list)) { list = new(); childMap[app.Id] = list; }
            list.Add(new AppProcess { Pid = pid, BytesIn = val.In, BytesOut = val.Out, DownRate = di, UpRate = dono });
        }
        _prevPerPid = snap;

        foreach (var (appId, list) in childMap)
            if (_appStore.TryGetValue(appId, out var u))
                u.Processes = list.OrderByDescending(p => p.BytesIn + p.BytesOut).ToList();

        // Decay the "active" flag for apps that didn't move this tick.
        foreach (var u in _appStore.Values)
            if (u.LastSeen < now - TimeSpan.FromSeconds(3)) u.IsActive = false;

        // Append this tick's combined rate to each app's sparkline ring. Rebuild the
        // list (copy-on-write) so the IPC thread can read a stable snapshot lock-free.
        foreach (var u in _appStore.Values)
        {
            int rate = (int)Math.Clamp(u.DownRate + u.UpRate, 0, int.MaxValue);
            var prev = u.RateHistory;
            int keep = Math.Min(prev.Count, SparkHistorySeconds - 1);
            var next = new List<int>(keep + 1);
            for (int k = prev.Count - keep; k < prev.Count; k++) next.Add(prev[k]);
            next.Add(rate);
            u.RateHistory = next;
        }
    }

    /// <summary>Drop PID-keyed caches (resolver + ETW counters) for exited processes.</summary>
    private void PruneProcessCaches()
    {
        var alive = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            alive.Add(p.Id);
            p.Dispose();
        }
        _processes.PruneDeadPids(alive);
        _etw.PrunePids(alive);
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
        long day = LocalDayStart(bucket);

        if (_pendingGlobalIn > 0 || _pendingGlobalOut > 0)
        {
            _store.AddGlobalMinute(bucket, _pendingGlobalIn, _pendingGlobalOut);
            _store.AddGlobalDay(day, _pendingGlobalIn, _pendingGlobalOut);
        }
        _pendingGlobalIn = _pendingGlobalOut = 0;

        foreach (var (appId, v) in _pendingApp)
        {
            _store.AddAppMinute(appId, bucket, v.In, v.Out);
            _store.AddAppDay(appId, day, v.In, v.Out);
        }
        _pendingApp.Clear();

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (ip, v) in _pendingHost)
        {
            _store.AddHostMinute(ip, bucket, v.In, v.Out);
            var geo = _settings.ResolveGeoIp ? _geo.Resolve(ip) : GeoInfo.Unknown;
            var host = _dns.Resolve(ip);
            _store.UpsertHostMeta(ip, ip, host, geo);
            if (geo.HasCountry) _store.TouchCountry(geo.CountryCode, nowUnix);
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

                if (!_appStore.TryGetValue(grp.Key, out var u))
                {
                    var app = _processes.Resolve(grp.First().ProcessId);
                    if (app.Id is "system" or "unknown") continue;
                    u = GetOrCreateApp(app, now);
                    u.LastSeen = now;
                }

                // Most-contacted host + distinct host count.
                var topHost = grp.GroupBy(c => c.RemoteDisplay).OrderByDescending(g => g.Count()).First();
                u.PrimaryHost = topHost.Key;
                u.PrimaryHostCountry = topHost.First().Geo.CountryCode;
                u.HostCount = grp.Select(c => c.RemoteDisplay).Distinct().Count();

                // Fallback child-process list from the connection table (no ETW bytes).
                if (u.Processes.Count == 0)
                    u.Processes = grp.Select(c => c.ProcessId).Distinct()
                                     .Select(p => new AppProcess { Pid = p }).ToList();
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

        if (_settings.MonitorHostsFile)
        {
            var a = _integrity.CheckHostsFile();
            if (a is not null) Persist(a);
        }

        if (_settings.MonitorArpSpoofing)
            foreach (var a in _integrity.CheckGatewayArp()) Persist(a);

        if (_settings.MonitorProxyChanges)
        {
            var a = _integrity.CheckProxy();
            if (a is not null) Persist(a);
        }

        if (_settings.DataPlan.Enabled)
        {
            _settings.DataPlan.UsedBytes = _store.DataPlanUsed(CycleStartBucket());
            var a = _alerts.CheckDataPlan(_settings.DataPlan);
            if (a is not null) Persist(a);
        }

        RefreshNetwork();
        RefreshFirewallCache();
        EvictIdleApps();
    }

    /// <summary>
    /// Evict apps that have been idle for a while so the live store (and the per-tick
    /// RateHistory rebuild) stays bounded to recently-active apps. Historical totals for
    /// evicted apps remain in SQLite and re-appear on demand; if such an app reconnects
    /// it is simply re-created.
    /// </summary>
    private void EvictIdleApps()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var kv in _appStore)
        {
            var u = kv.Value;
            if (!u.IsActive && u.ActiveConnections == 0 && u.LastSeen < cutoff)
                _appStore.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Drop history rows older than the configured retention (best effort).</summary>
    private void PruneHistory()
    {
        int days = _settings.HistoryRetentionDays;
        if (days <= 0) return; // retention disabled: keep everything
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long minuteCutoff = now - (long)days * 86400;
            // Never prune minute rows the active monthly data plan still needs to sum
            // (DataPlanUsed reads only traffic_min back to the billing-cycle start).
            if (_settings.DataPlan.Enabled)
                minuteCutoff = Math.Min(minuteCutoff, CycleStartBucket());
            long dayCutoff = LocalDayStart(now) - Math.Max(days, 730) * 86400L; // keep ≥2y of daily rollups
            int removed = _store.PruneOldHistory(minuteCutoff, dayCutoff);
            if (removed > 0) Console.WriteLine($"[Engine] pruned {removed} history rows older than {days}d.");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Engine] prune: {ex.Message}"); }
    }

    /// <summary>Re-detect the active network and auto-activate a matching profile.</summary>
    private void RefreshNetwork()
    {
        try
        {
            _network = NetworkIdentity.Current();
            if (string.IsNullOrEmpty(_network.Fingerprint)) return;

            string? toActivate = null;
            lock (_settingsLock)
            {
                // Already on a profile bound to this network → leave the user's choice alone.
                var active = ActiveProfileObj();
                if (active is not null && active.AutoActivateOnNetwork.Equals(_network.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    return;

                var match = _settings.FirewallProfiles.FirstOrDefault(
                    p => !string.IsNullOrEmpty(p.AutoActivateOnNetwork) &&
                         p.AutoActivateOnNetwork.Equals(_network.Fingerprint, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !match.Name.Equals(_settings.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                    toActivate = match.Name;
            }
            if (toActivate is not null) ActivateProfile(toActivate);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Engine] network refresh: {ex.Message}"); }
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
            var next = new ConcurrentDictionary<string, AppFirewallRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rules) next[r.AppId] = r;
            _firewallCache = next; // atomic reference swap: readers see the old or new map, never half-filled
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

        long wan, lan;
        if (_etwActive && _etw.IsRunning) { (wan, lan) = _etw.ReadWanLan(); }
        else { wan = totIn + totOut + _pendingGlobalIn + _pendingGlobalOut; lan = 0; }

        return new EngineStatus
        {
            MachineName = Environment.MachineName,
            EngineVersion = EngineVersion,
            FirewallMode = _settings.FirewallMode,
            DownloadBytesPerSec = downBps,
            UploadBytesPerSec = upBps,
            TotalBytesIn = totIn + _pendingGlobalIn,
            TotalBytesOut = totOut + _pendingGlobalOut,
            TotalWanBytes = wan,
            TotalLanBytes = lan,
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

        // Apps (with live session overlay: active flag, connection count, firewall status).
        var apps = _store.QueryUsageByApp(fromBucket, toBucket);
        var byId = apps.ToDictionary(a => a.App.Id, StringComparer.OrdinalIgnoreCase);
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
            a.DownRate = live.DownRate;
            a.UpRate = live.UpRate;
            a.PrimaryHost = live.PrimaryHost;
            a.PrimaryHostCountry = live.PrimaryHostCountry;
            a.HostCount = live.HostCount;
            a.Processes = live.Processes;
            a.RateHistory = live.RateHistory; // stable snapshot ref (copy-on-write)
        }
        foreach (var a in apps)
        {
            a.FirewallStatus = FirewallStatusFor(a.App.Id);
            a.Reputation = _reputation.Get(a.App.ExecutablePath);
        }
        resp.Apps = apps.OrderByDescending(a => a.Total).ToList();

        // Hosts, traffic types and countries all derive from the host rollup.
        resp.Hosts = _store.QueryUsageByHost(fromBucket, toBucket);
        resp.Types = BuildTrafficTypes(resp.Hosts);
        resp.Countries = BuildCountries(resp.Hosts);

        resp.TotalBytesIn = resp.Apps.Sum(a => a.BytesIn);
        resp.TotalBytesOut = resp.Apps.Sum(a => a.BytesOut);
        return resp;
    }

    private List<CountryUsage> BuildCountries(List<HostUsage> hosts)
    {
        var map = new Dictionary<string, CountryUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hosts)
        {
            string code = h.Geo.HasCountry ? h.Geo.CountryCode : "??";
            if (!map.TryGetValue(code, out var cu))
            {
                cu = new CountryUsage
                {
                    CountryCode = h.Geo.HasCountry ? code : "",
                    CountryName = h.Geo.HasCountry ? h.Geo.CountryName : "Unknown",
                };
                map[code] = cu;
            }
            cu.BytesIn += h.BytesIn;
            cu.BytesOut += h.BytesOut;
        }

        // Add the LAN bucket ("Local network") from this session's WAN/LAN split.
        if (_etwActive && _etw.IsRunning)
        {
            var (_, lan) = _etw.ReadWanLan();
            if (lan > 0)
                map["__local"] = new CountryUsage { CountryName = "Local network", IsLocal = true, BytesIn = lan };
        }

        long total = map.Values.Sum(c => c.Total);
        foreach (var c in map.Values) c.Fraction = total > 0 ? (double)c.Total / total : 0;
        return map.Values.OrderByDescending(c => c.Total).ToList();
    }

    // ---------------- analytics / anomalies ----------------

    /// <summary>
    /// Build the analytics report for a range: totals, hour-of-day and per-day
    /// patterns, top apps, detected anomalies and plain-language highlights. Pure
    /// read over the recorded rollups, so it works without elevation too.
    /// </summary>
    public InsightsReport GetInsights(GraphRange range, long fromUnix = 0, long toUnix = 0)
    {
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A custom [From, To] window overrides the preset range (which always ends "now").
        bool custom = fromUnix > 0 && toUnix > fromUnix;
        long windowSec = custom ? toUnix - fromUnix : (long)range.Duration().TotalSeconds;
        long fromBucket = (custom ? fromUnix : nowSec - windowSec) / 60 * 60;
        long toBucket = (custom ? toUnix : nowSec) / 60 * 60;
        long todayStart = LocalDayStart(nowSec);

        var report = new InsightsReport { Range = range };
        var rows = _store.QueryGlobal(fromBucket, toBucket);

        // Hour-of-day (local) and per-local-day breakdown.
        var hoursIn = new long[24];
        var hoursOut = new long[24];
        var dayMap = new SortedDictionary<long, (long In, long Out)>();
        foreach (var (bucket, inB, outB) in rows)
        {
            int hr = DateTimeOffset.FromUnixTimeSeconds(bucket).ToLocalTime().Hour;
            hoursIn[hr] += inB; hoursOut[hr] += outB;
            long day = LocalDayStart(bucket);
            dayMap.TryGetValue(day, out var d);
            dayMap[day] = (d.In + inB, d.Out + outB);
        }
        for (int h = 0; h < 24; h++)
            report.HourOfDay.Add(new HourUsage { Hour = h, BytesIn = hoursIn[h], BytesOut = hoursOut[h] });
        foreach (var (day, d) in dayMap)
            report.Daily.Add(new DayUsage { DayStartUnix = day, BytesIn = d.In, BytesOut = d.Out });

        // Per-bar app breakdowns for the chart hover tooltips (top few apps per hour / per day).
        static List<AppShare> Top4(IEnumerable<(string Name, string Path, long In, long Out)> g) =>
            g.OrderByDescending(a => a.In + a.Out).Take(4)
             .Select(a => new AppShare { Name = a.Name, ExecutablePath = a.Path, BytesIn = a.In, BytesOut = a.Out })
             .ToList();

        var byHour = _store.QueryAppByHour(fromBucket, toBucket)
            .GroupBy(a => a.Hour)
            .ToDictionary(g => g.Key, g => Top4(g.Select(a => (a.Name, a.Path, a.In, a.Out))));
        foreach (var hu in report.HourOfDay)
            if (byHour.TryGetValue(hu.Hour, out var hourTop)) hu.TopApps = hourTop;

        var byDay = _store.QueryAppByDay(fromBucket, toBucket)
            .GroupBy(a => a.Day)
            .ToDictionary(g => g.Key, g => Top4(g.Select(a => (a.Name, a.Path, a.In, a.Out))));
        foreach (var du in report.Daily)
        {
            // Match SQLite's strftime('%Y-%m-%d') key, which is always proleptic-Gregorian:
            // format with the invariant culture so a non-Gregorian OS calendar (Thai, Umm-al-Qura)
            // can't produce a mismatched key that leaves every daily tooltip's app list empty.
            string key = DateTimeOffset.FromUnixTimeSeconds(du.DayStartUnix).ToLocalTime()
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (byDay.TryGetValue(key, out var dayTop)) du.TopApps = dayTop;
        }

        report.TotalBytesIn = rows.Sum(r => r.In);
        report.TotalBytesOut = rows.Sum(r => r.Out);
        report.ActiveDays = dayMap.Count;
        report.AveragePerActiveDay = report.ActiveDays > 0 ? report.TotalBytes / report.ActiveDays : 0;

        int busiest = -1; long peak = 0;
        for (int h = 0; h < 24; h++)
        {
            long t = hoursIn[h] + hoursOut[h];
            if (t > peak) { peak = t; busiest = h; }
        }
        report.BusiestHour = busiest;

        // Previous equal-length window, for the period-over-period delta. End one minute
        // before fromBucket so the two windows partition cleanly (QueryGlobal is inclusive).
        long prevFrom = fromBucket - windowSec;
        var prevRows = _store.QueryGlobal(prevFrom, fromBucket - 60);
        report.PreviousTotalBytes = prevRows.Sum(r => r.In + r.Out);
        report.ChangeFraction = report.PreviousTotalBytes > 0
            ? (double)(report.TotalBytes - report.PreviousTotalBytes) / report.PreviousTotalBytes
            : 0;

        // Top apps in the window.
        var apps = _store.QueryUsageByApp(fromBucket, toBucket);
        report.ActiveApps = apps.Count;
        long appGrand = apps.Sum(a => a.Total);
        foreach (var a in apps.OrderByDescending(a => a.Total).Take(8))
            report.TopApps.Add(new AppShare
            {
                AppId = a.App.Id,
                Name = a.App.Name,
                ExecutablePath = a.App.ExecutablePath,
                BytesIn = a.BytesIn,
                BytesOut = a.BytesOut,
                Fraction = appGrand > 0 ? (double)a.Total / appGrand : 0,
            });

        // Anomaly detection compares *today* against a trailing baseline, so it is only
        // meaningful when the reported window reaches into today. A custom window that ends in
        // the past would otherwise show today's live anomalies beside unrelated historical data.
        bool windowIncludesToday = !custom || toBucket >= todayStart / 60 * 60;
        report.Anomalies = windowIncludesToday
            ? ComputeAnomalies(nowSec, todayStart, rows)
            : new List<UsageAnomaly>();
        report.Highlights = BuildHighlights(report);
        return report;
    }

    /// <summary>Compute today's anomalies against the trailing baseline (used by both
    /// the Analytics report and the periodic alert scan).</summary>
    private List<UsageAnomaly> ComputeAnomalies(long nowSec, long todayStart, List<(long Bucket, long In, long Out)> windowRows)
    {
        long todayStartBucket = todayStart / 60 * 60;
        long nowBucket = nowSec / 60 * 60;
        var today = _store.QueryUsageByApp(todayStartBucket, nowBucket);

        const int BaselineDays = 14;
        long baseFromDay = LocalDayStart(nowSec - BaselineDays * 86400L);
        var baselines = _store.AppDailyBaseline(baseFromDay, todayStart)
            .Select(b => new AnomalyDetector.Baseline(b.AppId, b.ActiveDays, b.In, b.Out))
            .ToList();

        long countryCutoff = nowSec - 86400;
        var newCountries = _store.NewCountriesSince(countryCutoff)
            .Select(c => new AnomalyDetector.NewCountry(c.Code, _store.CountryName(c.Code), c.FirstSeen))
            .ToList();

        var baseHours = NewHourList();
        var todayHours = NewHourList();
        var baseDays = new HashSet<long>();
        foreach (var (bucket, inB, outB) in windowRows)
        {
            int hr = DateTimeOffset.FromUnixTimeSeconds(bucket).ToLocalTime().Hour;
            if (bucket >= todayStart) { todayHours[hr].BytesIn += inB; todayHours[hr].BytesOut += outB; }
            else { baseHours[hr].BytesIn += inB; baseHours[hr].BytesOut += outB; baseDays.Add(LocalDayStart(bucket)); }
        }

        return AnomalyDetector.Detect(today, baselines, baseDays.Count, newCountries, baseHours, todayHours);
    }

    private static List<HourUsage> NewHourList()
    {
        var list = new List<HourUsage>(24);
        for (int h = 0; h < 24; h++) list.Add(new HourUsage { Hour = h });
        return list;
    }

    private static List<string> BuildHighlights(InsightsReport r)
    {
        var h = new List<string>();
        if (r.TotalBytes == 0)
        {
            h.Add("No recorded network activity in this period yet.");
            return h;
        }

        if (r.BusiestHour >= 0)
            h.Add($"Your network is busiest around {r.BusiestHour:00}:00.");

        if (r.TopApps.Count > 0 && r.TopApps[0].Fraction > 0)
            h.Add($"{r.TopApps[0].Name} accounts for {r.TopApps[0].Fraction:P0} of traffic ({ByteFormatter.Bytes(r.TopApps[0].Total)}).");

        if (r.PreviousTotalBytes > 0)
        {
            string dir = r.ChangeFraction >= 0 ? "up" : "down";
            h.Add($"Usage is {dir} {Math.Abs(r.ChangeFraction):P0} versus the previous {r.Range.Label().ToLowerInvariant()} " +
                  $"({ByteFormatter.Bytes(r.TotalBytes)} vs {ByteFormatter.Bytes(r.PreviousTotalBytes)}).");
        }

        if (r.ActiveDays > 0)
            h.Add($"Averaging {ByteFormatter.Bytes(r.AveragePerActiveDay)} per active day over {r.ActiveDays} day(s).");

        if (r.TotalBytesIn > 0 && r.TotalBytesOut > 0)
            h.Add($"Split: {ByteFormatter.Bytes(r.TotalBytesIn)} down · {ByteFormatter.Bytes(r.TotalBytesOut)} up.");

        if (r.Anomalies.Count > 0)
            h.Add($"{r.Anomalies.Count} usage anomal{(r.Anomalies.Count == 1 ? "y" : "ies")} detected — see below.");

        return h;
    }

    // ---------------- GeoIP database ----------------

    /// <summary>Current GeoIP database status for the Settings screen.</summary>
    public GeoIpStatusResponse GetGeoIpStatus() => new()
    {
        Available = _geo.Available,
        Source = GeoSourceLabel(),
        DatabaseType = _geo.DatabaseType,
        BuildDate = _geo.BuildDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "",
        LastUpdateUnix = _settings.GeoIpLastUpdateUnix,
        AutoUpdate = _settings.GeoIpAutoUpdate,
    };

    private string GeoSourceLabel()
    {
        var type = _geo.DatabaseType;
        if (type.Contains("DBIP", StringComparison.OrdinalIgnoreCase)) return "DB-IP Lite";
        if (type.Contains("GeoLite", StringComparison.OrdinalIgnoreCase)) return "MaxMind GeoLite2";
        return _geo.Available ? (string.IsNullOrEmpty(type) ? "GeoIP" : type) : "";
    }

    /// <summary>Download + install the latest free country database on demand, then return the new
    /// status. Serialized so concurrent requests don't collide; the active database survives failures.</summary>
    public async Task<GeoIpStatusResponse> UpdateGeoIpAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _geoUpdating, 1, 0) != 0)
        {
            var busy = GetGeoIpStatus();
            busy.Success = false;
            busy.Message = "An update is already in progress.";
            return busy;
        }
        try
        {
            var result = await _geoUpdater.UpdateAsync(DateTimeOffset.UtcNow.UtcDateTime, _geo.BuildDate, ct)
                .ConfigureAwait(false);

            // A completed check (installed a newer DB or confirmed we're current) stamps the time,
            // so auto-update won't re-check every launch. A network/validation failure does not.
            if (result.Success)
            {
                lock (_settingsLock)
                {
                    _settings.GeoIpLastUpdateUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _store.SaveSettings(_settings);
                }
            }

            var status = GetGeoIpStatus();
            status.Success = result.Success;
            status.Updated = result.Updated;
            status.Message = result.Message;
            return status;
        }
        finally
        {
            Interlocked.Exchange(ref _geoUpdating, 0);
        }
    }

    /// <summary>Best-effort auto-update on startup: at most once every ~25 days, silent on failure.</summary>
    private async Task AutoUpdateGeoIpAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); // let startup settle
            long last = _settings.GeoIpLastUpdateUnix;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (last > 0 && now - last < 25 * 86400L) return; // checked recently
            await UpdateGeoIpAsync(ct).ConfigureAwait(false);
        }
        catch { /* best effort — GeoIP stays on the current database */ }
    }

    /// <summary>Periodic scan that raises freshly-detected usage anomalies as alerts
    /// (each distinct anomaly at most once per local day).</summary>
    private void RunUsageAnomalyChecks()
    {
        if (!_settings.MonitorUsageAnomalies) return;
        try
        {
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long todayStart = LocalDayStart(nowSec);
            long fromBucket = (nowSec - 14 * 86400L) / 60 * 60;
            var rows = _store.QueryGlobal(fromBucket, nowSec / 60 * 60);
            var anomalies = ComputeAnomalies(nowSec, todayStart, rows);

            if (_anomalyDayStamp != todayStart) { _anomaliesAlertedToday.Clear(); _anomalyDayStamp = todayStart; }

            foreach (var an in anomalies)
            {
                if (!_anomaliesAlertedToday.Add(an.DedupeKey)) continue;
                Persist(new Alert
                {
                    Time = DateTimeOffset.UtcNow,
                    Kind = AlertKind.UsageAnomaly,
                    Severity = an.Severity,
                    Title = an.Title,
                    Message = an.Detail,
                    AppId = string.IsNullOrEmpty(an.AppId) ? null : an.AppId,
                    AppName = string.IsNullOrEmpty(an.AppName) ? null : an.AppName,
                });
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Engine] anomaly scan: {ex.Message}"); }
    }

    public HardwareSnapshot GetHardware() => _hardware.GetSnapshot();

    /// <summary>Throttle the high-frequency hardware / per-process samplers when the UI isn't being
    /// viewed. The 1-second traffic tick (core recording) is never gated.</summary>
    public void SetUiActive(bool active) => _hardware.SetUiActive(active);

    private List<TrafficTypeUsage> BuildTrafficTypes(List<HostUsage> hosts)
    {
        // Byte-accurate protocol split from ETW per-port-class counters (elevated).
        if (_etwActive && _etw.IsRunning)
        {
            var classes = _etw.SnapshotPortClasses();
            if (classes.Count > 0)
            {
                long total = classes.Sum(c => c.In + c.Out);
                var result = classes
                    .Select(c => new TrafficTypeUsage { TypeName = c.Name, BytesIn = c.In, BytesOut = c.Out })
                    .OrderByDescending(t => t.Total).ToList();
                foreach (var t in result) t.Fraction = total > 0 ? (double)t.Total / total : 0;
                return result;
            }
        }

        // Fallback (no ETW): approximate from the live connection table by port class.
        long totalIn = hosts.Sum(h => h.BytesIn);
        long totalOut = hosts.Sum(h => h.BytesOut);
        long grand = totalIn + totalOut;
        var byClass = _connectionsSnapshot.Where(ConnectionEnumerator.HasRemotePeer)
            .GroupBy(c => TrafficClassifier.Classify(c.RemotePort))
            .ToDictionary(g => g.Key, g => g.Count());
        int conns = Math.Max(1, byClass.Values.Sum());
        var fb = byClass.OrderByDescending(kv => kv.Value)
            .Select(kv => new TrafficTypeUsage
            {
                TypeName = kv.Key,
                BytesIn = (long)(totalIn * ((double)kv.Value / conns)),
                BytesOut = (long)(totalOut * ((double)kv.Value / conns)),
            }).ToList();
        if (fb.Count == 0) fb.Add(new TrafficTypeUsage { TypeName = "Other", BytesIn = totalIn, BytesOut = totalOut });
        foreach (var t in fb) t.Fraction = grand > 0 ? (double)t.Total / grand : 0;
        return fb;
    }

    public List<ConnectionInfo> GetConnections() =>
        _connectionsSnapshot.Where(ConnectionEnumerator.HasRemotePeer).ToList();

    public (FirewallStatus Status, List<AppFirewallRule> Rules, List<FirewallProfile> Profiles) GetFirewall()
    {
        bool lockdown = CanEnforceFirewall && _firewall.IsBlockAllActive();
        FirewallStatus status;
        List<FirewallProfile> profiles;
        lock (_settingsLock)
        {
            status = new FirewallStatus
            {
                Mode = _settings.FirewallMode,
                ActiveProfile = _settings.ActiveProfile,
                NetworkName = _network.Name,
                NetworkFingerprint = _network.Fingerprint,
                BlockedAppCount = _firewallCache.Count,
                PendingAppCount = _pendingApps.Count,
                CanEnforce = CanEnforceFirewall,
                LockdownActive = lockdown,
            };
            // Deep-copy so the IPC writer thread serializes a stable snapshot even while
            // another thread mutates the live profile set / blocked-app lists.
            profiles = _settings.FirewallProfiles.Select(p => CloneProfile(p, _settings.ActiveProfile)).ToList();
        }
        return (status, _firewallCache.Values.ToList(), profiles);
    }

    private static FirewallProfile CloneProfile(FirewallProfile p, string activeName) => new()
    {
        Name = p.Name,
        AutoActivateOnNetwork = p.AutoActivateOnNetwork,
        NetworkLabel = p.NetworkLabel,
        Mode = p.Mode,
        BlockedAppIds = new List<string>(p.BlockedAppIds),
        IsActive = p.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase),
    };

    // ---------------- firewall profiles ----------------

    /// <summary>Guarantee a "Default" profile exists and an active profile is set.</summary>
    private void EnsureProfiles()
    {
      lock (_settingsLock)
      {
        bool changed = false;
        if (_settings.FirewallProfiles.Count == 0)
        {
            _settings.FirewallProfiles.Add(new FirewallProfile { Name = "Default", Mode = _settings.FirewallMode });
            changed = true;
        }
        if (string.IsNullOrEmpty(_settings.ActiveProfile) ||
            !_settings.FirewallProfiles.Any(p => p.Name.Equals(_settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.ActiveProfile = _settings.FirewallProfiles[0].Name;
            changed = true;
        }
        // Best-effort: the profiles exist in memory regardless; persistence can fail
        // (e.g. a read-only DB when running non-elevated) and must not crash startup.
        if (changed)
            try { _store.SaveSettings(_settings); }
            catch (Exception ex) { Console.Error.WriteLine($"[Engine] persist profiles: {ex.Message}"); }
      }
    }

    // Guarded by _settingsLock (reentrant): callers already hold it.
    private FirewallProfile? ActiveProfileObj()
    {
        lock (_settingsLock)
            return _settings.FirewallProfiles.FirstOrDefault(p => p.Name.Equals(_settings.ActiveProfile, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveProfile(FirewallProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) return;
        lock (_settingsLock)
        {
            var list = _settings.FirewallProfiles;
            int idx = list.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) list[idx] = profile; else list.Add(profile);
            _store.SaveSettings(_settings);
        }
    }

    public void DeleteProfile(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return;
        lock (_settingsLock)
        {
            _settings.FirewallProfiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (_settings.ActiveProfile.Equals(name, StringComparison.OrdinalIgnoreCase))
                ActivateProfile("Default");
            else
                _store.SaveSettings(_settings);
        }
    }

    public void ActivateProfile(string name)
    {
        lock (_settingsLock)
        {
            var prof = _settings.FirewallProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (prof is null) return;

            _settings.ActiveProfile = prof.Name;
            _settings.FirewallMode = prof.Mode;
            ApplyProfileBlocks(prof);
            _store.SaveSettings(_settings);
        }

        _status = BuildStatus(DateTimeOffset.UtcNow, 0, 0);
        Events?.Invoke(new StatusChangedEvent { Status = _status });
    }

    /// <summary>Reconcile enforced blocks so exactly the profile's app set is blocked.</summary>
    private void ApplyProfileBlocks(FirewallProfile prof)
    {
        if (!CanEnforceFirewall) return;
        var desired = new HashSet<string>(prof.BlockedAppIds, StringComparer.OrdinalIgnoreCase);

        foreach (var appId in _firewallCache.Keys.ToList())
            if (!desired.Contains(appId)) _firewall.UnblockApp(appId);

        foreach (var appId in desired)
        {
            if (_firewallCache.ContainsKey(appId)) continue;
            string exe = _appStore.TryGetValue(appId, out var u) ? u.App.ExecutablePath : appId;
            string nm = _appStore.TryGetValue(appId, out var au) ? au.App.Name : Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrEmpty(exe))
                try { _firewall.SetAppBlocked(exe, appId, nm, blockIn: true, blockOut: true); } catch { }
        }
        RefreshFirewallCache();
    }

    private AppFirewallStatus FirewallStatusFor(string appId)
    {
        if (_pendingApps.ContainsKey(appId)) return AppFirewallStatus.Pending;
        if (_firewallCache.ContainsKey(appId)) return AppFirewallStatus.Blocked;
        return AppFirewallStatus.Allowed;
    }

    public void SetFirewallMode(FirewallMode mode)
    {
        FirewallMode old;
        lock (_settingsLock)
        {
            old = _settings.FirewallMode;
            _settings.FirewallMode = mode;
            var active = ActiveProfileObj();
            if (active is not null) active.Mode = mode;
            _store.SaveSettings(_settings);
        }

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

    /// <summary>Engage or lift a global lock-down — a single WFP rule that blocks every application in
    /// both directions, overlaying the per-app rules (which resume when it is lifted).</summary>
    public void SetLockdown(bool on)
    {
        if (!CanEnforceFirewall) return;
        try { _firewall.SetBlockAll(on); }
        catch (Exception ex) { Console.Error.WriteLine($"[Engine] lockdown: {ex.Message}"); return; }
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

        // Record the decision in the active profile so it survives restarts and
        // profile switches.
        lock (_settingsLock)
        {
            var prof = ActiveProfileObj();
            if (prof is not null)
            {
                prof.BlockedAppIds.RemoveAll(a => a.Equals(appId, StringComparison.OrdinalIgnoreCase));
                if (blockIn || blockOut) prof.BlockedAppIds.Add(appId);
                _store.SaveSettings(_settings);
            }
        }
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

    public AppSettings GetSettings()
    {
        // Return a deep copy so the IPC writer thread never serializes a graph another
        // thread is mutating (which throws "Collection was modified").
        lock (_settingsLock)
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings)) ?? new AppSettings();
    }

    public void SetSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_settingsLock)
        {
            _store.SaveSettings(settings);
            _settings = settings;
        }
        _dns.Enabled = settings.ResolveHostNames;
        _reputation.SetApiKey(settings.VirusTotalApiKey);
        SetFirewallMode(settings.FirewallMode);
    }

    // ---------------- storage management ----------------

    public StorageInfo GetStorageInfo()
    {
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(_dataDir)!).AvailableFreeSpace; } catch { }
        return new StorageInfo
        {
            DataDirectory = _dataDir,
            DatabaseBytes = _store.DatabaseBytes(),
            FreeBytes = free,
            OldestRecord = _store.OldestRecordUtc(),
            RestartRequired = false,
        };
    }

    /// <summary>Clear recorded history and compact the database in place.</summary>
    public StorageInfo ClearData(ClearDataMode mode)
    {
        _store.ClearData(mode);
        return GetStorageInfo();
    }

    /// <summary>
    /// Stage a move of the database to a new directory. The live engine keeps using
    /// the current file; the copy + the persisted pointer take effect on restart
    /// (moving an open SQLite database out from under the engine is not safe).
    /// </summary>
    public StorageInfo RelocateStorage(string newDir)
    {
        var info = GetStorageInfo();
        if (string.IsNullOrWhiteSpace(newDir)) return info;
        newDir = StorageSecurity.PrepareDataDirectory(newDir);
        if (string.Equals(newDir, _dataDir, StringComparison.OrdinalIgnoreCase)) return info;

        string targetDb = Path.Combine(newDir, "openwire.db");
        string tempDb = Path.Combine(newDir, $".openwire.{Guid.NewGuid():N}.tmp");
        try
        {
            _store.BackupTo(tempDb);
            StorageSecurity.RejectReparseFile(targetDb);
            DeleteStaleSidecar(targetDb + "-wal");
            DeleteStaleSidecar(targetDb + "-shm");
            File.Move(tempDb, targetDb, overwrite: true);
            StorageSecurity.EnsurePrivateFile(targetDb);
            long targetBytes = new FileInfo(targetDb).Length;

            // Persist only after a verified database is atomically in place.
            StorageSecurity.WritePrivateTextFileAtomic(DataDirPointerPath, newDir);
            info.DataDirectory = newDir;
            info.DatabaseBytes = targetBytes;
            info.RestartRequired = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[storage] relocate failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
        return info;
    }

    private static void DeleteStaleSidecar(string path)
    {
        if (!File.Exists(path)) return;
        StorageSecurity.RejectReparseFile(path);
        File.Delete(path);
    }

    /// <summary>Fixed pointer file recording where the data directory lives, so the
    /// engine can find a relocated database on the next launch.</summary>
    public static string DataDirPointerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenWire", "datadir.txt");

    // ---------------- helpers ----------------

    private static long MinuteBucket(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60 * 60;

    /// <summary>Unix seconds at local midnight of the day containing <paramref name="unixSeconds"/>.
    /// Resolves the UTC offset at midnight (not at the input instant) so the boundary is
    /// correct on DST-transition days.</summary>
    private static long LocalDayStart(long unixSeconds)
    {
        var localMidnight = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().Date;
        var offset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset).ToUnixTimeSeconds();
    }

    private long CycleStartBucket()
    {
        // Anchor the billing cycle to LOCAL midnight of the reset day, consistent with
        // every other day boundary (LocalDayStart), so usage isn't off by the tz offset.
        var nowLocal = DateTime.Now;
        int day = Math.Clamp(_settings.DataPlan.BillingCycleStartDay, 1, 28);
        var startLocal = new DateTime(nowLocal.Year, nowLocal.Month, day, 0, 0, 0, DateTimeKind.Local);
        if (startLocal > nowLocal) startLocal = startLocal.AddMonths(-1);
        return MinuteBucket(new DateTimeOffset(startLocal));
    }

    private static void Accumulate(Dictionary<string, (long In, long Out)> map, string key, long inB, long outB)
    {
        map.TryGetValue(key, out var cur);
        map[key] = (cur.In + inB, cur.Out + outB);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the tick loop and wait for it before the final flush, so the pending
        // dictionaries are never mutated from two threads at once.
        _engineCts?.Cancel();
        if (_tickLoop is not null)
        {
            try { await _tickLoop.ConfigureAwait(false); } catch { /* cancelled */ }
        }

        try { FlushMinute(_currentBucket); } catch { }
        // Never leave a user without the UI/control channel that can reverse lockdown.
        if (CanEnforceFirewall)
            try { _firewall.SetBlockAll(false); } catch { }
        _etw.Dispose();
        _reputation.Dispose();
        _hardware.Dispose();
        _geo.Dispose();
        _store.Dispose();
        _engineCts?.Dispose();
    }
}
