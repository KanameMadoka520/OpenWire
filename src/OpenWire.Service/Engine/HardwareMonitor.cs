using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Samples CPU / memory / disk / GPU utilisation adaptively (4 Hz while viewed,
/// low-frequency while idle) and keeps a short rolling graph history.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareMonitor : IDisposable
{
    /// <summary>Sample period in ms. 4 Hz (250 ms) so the live graphs advance in small,
    /// frequent steps that read as continuous instead of a once-a-second jump.</summary>
    private const int SampleIntervalMs = 250;

    private const int MaxHistory = 5 * 60 * 1000 / SampleIntervalMs + 20; // 5 min @ 4 Hz + margin
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Queue<HardwareSample> _history = new();
    private string _historyStreamId = Guid.NewGuid().ToString("N");
    private long _historySequence;
    private DateTimeOffset _lastHistoryTime;

    /// <summary>How often (in ticks) the GPU counter instance lists are re-enumerated (~5 s).</summary>
    private const int GpuRefreshTicks = 5000 / SampleIntervalMs;

    private PerformanceCounter? _cpu;
    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;

    // "GPU Engine" instances are per-process AND per-adapter AND per-engine-type
    // (pid_1234_luid_0x..._0x..._phys_0_engtype_3D / _Copy / _VideoEncode ...), and they
    // come and go as processes start and exit, so we key counters by instance name and
    // periodically re-enumerate instead of freezing the list at construction time.
    private readonly Dictionary<string, PerformanceCounter> _gpu = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter> _gpuMem = new(StringComparer.OrdinalIgnoreCase);
    private int _gpuTick;
    private int _sampling;

    private double _cpuV, _memPct, _diskR, _diskW, _gpuV;
    private long _memUsed, _memTotal, _gpuMemV;
    private Timer? _timer;
    private readonly ProcessResourceMonitor _procs = new();

    /// <summary>When the UI isn't being viewed (app in tray), sample slowly instead of at 4 Hz —
    /// enough to keep the 5-minute graph continuous when the user returns, at a fraction of the CPU.</summary>
    private const int IdleIntervalMs = 1500;
    private volatile bool _disposed;

    // ---- per-component inventory + utilisation for the "hardware resource usage" list ----
    // "% Disk Time" per physical disk (instances like "0 C:", "1 D:"), busy % clamped 0..100.
    private readonly Dictionary<string, PerformanceCounter> _diskBusy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _diskPct = new(StringComparer.OrdinalIgnoreCase);
    // Utilisation per GPU adapter, keyed by LUID ("0x{high}_0x{low}"), matched to names below.
    private readonly Dictionary<string, double> _gpuByLuid = new(StringComparer.OrdinalIgnoreCase);
    // GPU adapters (name + LUID) from DXGI, refreshed with the counter lists.
    private List<HardwareInventory.GpuAdapter> _gpuAdapters = new();

    public HardwareMonitor()
    {
        TryInit(ref _cpu, "Processor", "% Processor Time", "_Total");
        TryInit(ref _diskRead, "PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        TryInit(ref _diskWrite, "PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        RefreshGpuInstances();
        RefreshGpuMemoryInstances();
        RefreshDiskInstances();
        _gpuAdapters = HardwareInventory.EnumerateGpus();
    }

    /// <summary>Re-enumerate PhysicalDisk instances (per drive, e.g. "0 C:") for the
    /// per-disk busy% list; "_Total" is excluded (it's the aggregate).</summary>
    private void RefreshDiskInstances()
    {
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            var live = new HashSet<string>(cat.GetInstanceNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _diskBusy.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _diskBusy[stale].Dispose();
                _diskBusy.Remove(stale);
            }
            foreach (var inst in live)
            {
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                if (_diskBusy.ContainsKey(inst)) continue;
                try
                {
                    var c = new PerformanceCounter("PhysicalDisk", "% Disk Time", inst);
                    c.NextValue();
                    _diskBusy[inst] = c;
                }
                catch { /* instance vanished */ }
            }
        }
        catch { /* no disk counters */ }
    }

    public void Start()
    {
        // Idle is the safe default: the IPC server explicitly enables 4 Hz only while a visible
        // Hardware page asks for it. This also covers the startup grace period before any client.
        _timer = new Timer(_ => Sample(), null, IdleIntervalMs, IdleIntervalMs);
        _procs.Start();
        _procs.SetUiActive(false);
    }

    /// <summary>Throttle the samplers when the UI isn't actively viewing them. The hardware timer
    /// slows to <see cref="IdleIntervalMs"/> (a PDH rate counter self-normalizes over the measured
    /// elapsed time, so a slower interval doesn't misreport), and the per-process monitor pauses
    /// entirely. A full-interval dueTime avoids a sub-interval sample spike on resume.</summary>
    public void SetUiActive(bool active)
    {
        int period = active ? SampleIntervalMs : IdleIntervalMs;
        try { if (!_disposed) _timer?.Change(period, period); }
        catch (ObjectDisposedException) { }
        _procs.SetUiActive(active);
    }

    private static void TryInit(ref PerformanceCounter? counter, string cat, string name, string inst)
    {
        try { counter = new PerformanceCounter(cat, name, inst); counter.NextValue(); }
        catch { counter = null; }
    }

    /// <summary>
    /// Re-enumerates "GPU Engine" instances: disposes counters whose process has exited and
    /// adds counters for new instances. A freshly created <see cref="PerformanceCounter"/>
    /// needs two samples for a valid rate, so new instances are primed here and start
    /// contributing on the next tick.
    /// </summary>
    private void RefreshGpuInstances()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var live = new HashSet<string>(cat.GetInstanceNames(), StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _gpu.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _gpu[stale].Dispose();
                _gpu.Remove(stale);
            }

            foreach (var inst in live)
            {
                if (_gpu.ContainsKey(inst)) continue;
                PerformanceCounter? c = null;
                try
                {
                    c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    c.NextValue();
                    _gpu[inst] = c;
                }
                catch { c?.Dispose(); /* instance vanished mid-enumeration */ }
            }
        }
        catch { /* no GPU counters */ }
    }

    /// <summary>Re-enumerates "GPU Adapter Memory" instances (adapters can appear/disappear, e.g. virtual displays).</summary>
    private void RefreshGpuMemoryInstances()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Adapter Memory");
            var live = new HashSet<string>(cat.GetInstanceNames(), StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _gpuMem.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _gpuMem[stale].Dispose();
                _gpuMem.Remove(stale);
            }

            foreach (var inst in live)
            {
                if (_gpuMem.ContainsKey(inst)) continue;
                PerformanceCounter? c = null;
                try
                {
                    c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst);
                    c.NextValue();
                    _gpuMem[inst] = c;
                }
                catch { c?.Dispose(); /* skip */ }
            }
        }
        catch { /* no GPU memory counters */ }
    }

    /// <summary>
    /// Reduces an instance name like "pid_1234_luid_0x00000000_0x0000ABCD_phys_0_engtype_3D"
    /// to its (adapter, engine-type) group key "luid_0x00000000_0x0000ABCD_phys_0_engtype_3D",
    /// so per-process values can be summed per physical engine.
    /// </summary>
    private static string GpuGroupKey(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? instance[i..] : instance;
    }

    /// <summary>Extract the adapter LUID key ("0x{high}_0x{low}") from a GPU Engine
    /// instance name, to match DXGI's <see cref="HardwareInventory.GpuAdapter.LuidKey"/>.</summary>
    private static string GpuLuidKey(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int s = i + 5;
        int e = instance.IndexOf("_phys", s, StringComparison.OrdinalIgnoreCase);
        return e > s ? instance[s..e] : instance[s..];
    }

    /// <summary>
    /// Overall GPU utilisation with Task Manager semantics: sum the per-process values within
    /// each (adapter, engine-type) group, then report the busiest group, clamped to 0..100.
    /// Also fills <paramref name="perLuid"/> with each adapter's busiest engine group, so the
    /// resource list can show utilisation per physical GPU.
    /// </summary>
    private double SampleGpuUtilization(Dictionary<string, double> perLuid)
    {
        var groups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var groupLuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (inst, counter) in _gpu)
        {
            double v = SafeNext(counter);
            if (v <= 0) continue;
            string key = GpuGroupKey(inst);
            groups[key] = groups.TryGetValue(key, out double sum) ? sum + v : v;
            groupLuid[key] = GpuLuidKey(inst);
        }

        double busiest = 0;
        perLuid.Clear();
        foreach (var (key, val) in groups)
        {
            double clamped = Math.Clamp(val, 0, 100);
            busiest = Math.Max(busiest, clamped);
            string luid = groupLuid[key];
            if (luid.Length == 0) continue;
            perLuid[luid] = perLuid.TryGetValue(luid, out double cur) ? Math.Max(cur, clamped) : clamped;
        }
        return busiest;
    }

    private void Sample()
    {
        // The GPU dictionaries are only touched from this callback, but a slow tick could
        // overlap the next timer fire — skip instead of mutating them concurrently.
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            if (++_gpuTick >= GpuRefreshTicks)
            {
                _gpuTick = 0;
                RefreshGpuInstances();
                RefreshGpuMemoryInstances();
                RefreshDiskInstances();
                _gpuAdapters = HardwareInventory.EnumerateGpus();
            }

            double cpu = SafeNext(_cpu);
            double dr = SafeNext(_diskRead);
            double dw = SafeNext(_diskWrite);
            var perLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double gpu = SampleGpuUtilization(perLuid);

            // Per-disk busy% ("% Disk Time" can briefly exceed 100 under heavy queueing).
            var diskPct = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (inst, c) in _diskBusy)
                diskPct[inst] = Math.Clamp(SafeNext(c), 0, 100);

            // Single-number GPU memory = dedicated usage of the busiest adapter (max, not
            // sum): summing would mix multiple physical GPUs — and virtual adapters — into
            // one figure that no single card's VRAM capacity relates to.
            long gpuMem = 0;
            foreach (var c in _gpuMem.Values) gpuMem = Math.Max(gpuMem, (long)SafeNext(c));

            GetMemory(out long used, out long total);
            double memPct = total > 0 ? used * 100.0 / total : 0;

            lock (_lock)
            {
                _cpuV = cpu; _diskR = dr; _diskW = dw; _gpuV = gpu; _gpuMemV = gpuMem;
                _memUsed = used; _memTotal = total; _memPct = memPct;
                _gpuByLuid.Clear();
                foreach (var kv in perLuid) _gpuByLuid[kv.Key] = kv.Value;
                _diskPct.Clear();
                foreach (var kv in diskPct) _diskPct[kv.Key] = kv.Value;
                var sampleTime = NormalizeSampleTime(DateTimeOffset.UtcNow);

                var sample = new HardwareSample
                {
                    Sequence = ++_historySequence,
                    Time = sampleTime,
                    CpuPercent = cpu,
                    MemoryPercent = memPct,
                    DiskBytesPerSec = dr + dw,
                    GpuPercent = gpu,
                };
                _history.Enqueue(sample);
                while (_history.Count > MaxHistory
                    || (_history.Count > 0 && sample.Time - _history.Peek().Time > HistoryWindow))
                    _history.Dequeue();
            }
        }
        catch { /* transient counter error */ }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    /// <summary>Keep graph timestamps ordered. A material wall-clock rollback starts a new
    /// stream so clients discard points anchored to the old clock instead of drawing backwards.</summary>
    private DateTimeOffset NormalizeSampleTime(DateTimeOffset sampleTime)
    {
        if (_lastHistoryTime != default && sampleTime < _lastHistoryTime - TimeSpan.FromSeconds(1))
        {
            _history.Clear();
            _historySequence = 0;
            _historyStreamId = Guid.NewGuid().ToString("N");
        }
        else if (sampleTime <= _lastHistoryTime)
        {
            sampleTime = _lastHistoryTime.AddTicks(1);
        }
        _lastHistoryTime = sampleTime;
        return sampleTime;
    }

    private static double SafeNext(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; }
        catch { return 0; }
    }

    public HardwareSnapshot GetSnapshot(
        string? historyStreamId = null,
        long afterHistorySequence = 0,
        bool includeDetails = true,
        bool includeHistoryMetadata = true)
    {
        lock (_lock)
        {
            long oldest = _history.Count > 0 ? _history.Peek().Sequence : _historySequence + 1;
            bool canDelta = includeHistoryMetadata
                && string.Equals(historyStreamId, _historyStreamId, StringComparison.Ordinal)
                && afterHistorySequence >= oldest - 1
                && afterHistorySequence <= _historySequence;
            List<HardwareSample> history;
            if (!includeHistoryMetadata)
            {
                history = _history.Select(static s => new HardwareSample
                {
                    Time = s.Time,
                    CpuPercent = s.CpuPercent,
                    MemoryPercent = s.MemoryPercent,
                    DiskBytesPerSec = s.DiskBytesPerSec,
                    GpuPercent = s.GpuPercent,
                }).ToList();
            }
            else
            {
                history = canDelta
                    ? _history.Where(s => s.Sequence > afterHistorySequence).ToList()
                    : _history.ToList();
            }

            return new HardwareSnapshot
            {
                CpuPercent = _cpuV,
                MemoryPercent = _memPct,
                MemoryUsedBytes = _memUsed,
                MemoryTotalBytes = _memTotal,
                DiskReadBytesPerSec = _diskR,
                DiskWriteBytesPerSec = _diskW,
                GpuPercent = _gpuV,
                GpuMemoryUsedBytes = _gpuMemV,
                HistoryStreamId = includeHistoryMetadata ? _historyStreamId : null,
                LatestHistorySequence = includeHistoryMetadata ? _historySequence : 0,
                HistoryReset = includeHistoryMetadata && !canDelta,
                History = history,
                Processes = includeDetails ? _procs.Snapshot() : new List<ProcessResourceRow>(),
                Resources = includeDetails ? BuildResources() : new List<HardwareResourceRow>(),
            };
        }
    }

    /// <summary>Build the CPU / memory / GPU / disk resource rows. Called under _lock.</summary>
    private List<HardwareResourceRow> BuildResources()
    {
        var rows = new List<HardwareResourceRow>
        {
            new() { Kind = "cpu", Name = HardwareInventory.CpuName, Percent = _cpuV },
            new() { Kind = "memory", Percent = _memPct },
        };

        // GPUs in DXGI order; utilisation matched by LUID (idle adapters report no figure).
        if (_gpuAdapters.Count > 0)
        {
            for (int i = 0; i < _gpuAdapters.Count; i++)
            {
                var a = _gpuAdapters[i];
                double? pct = _gpuByLuid.TryGetValue(a.LuidKey, out var u) ? u : null;
                rows.Add(new HardwareResourceRow { Kind = "gpu", Name = a.Name, Detail = i.ToString(), Percent = pct });
            }
        }
        else if (_gpuV > 0)
        {
            rows.Add(new HardwareResourceRow { Kind = "gpu", Name = "GPU", Percent = _gpuV });
        }

        // Physical disks: instance "0 C:" -> number "0", drives "C:".
        foreach (var inst in _diskPct.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            int sp = inst.IndexOf(' ');
            string num = sp > 0 ? inst[..sp] : inst;
            string drives = sp > 0 ? inst[(sp + 1)..].Trim() : "";
            rows.Add(new HardwareResourceRow { Kind = "disk", Name = drives, Detail = num, Percent = _diskPct[inst] });
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static void GetMemory(out long used, out long total)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            total = (long)m.ullTotalPhys;
            used = (long)(m.ullTotalPhys - m.ullAvailPhys);
        }
        else { total = 0; used = 0; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _procs.Dispose();
        _cpu?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        foreach (var c in _gpu.Values) c.Dispose();
        foreach (var c in _gpuMem.Values) c.Dispose();
        foreach (var c in _diskBusy.Values) c.Dispose();
    }
}
