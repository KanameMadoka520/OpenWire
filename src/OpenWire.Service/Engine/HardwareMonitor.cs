using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Samples CPU / memory / disk / GPU utilisation once a second and keeps a short
/// rolling history for the Hardware Resources graphs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareMonitor : IDisposable
{
    private const int MaxHistory = 300; // 5 minutes @ 1 Hz

    private readonly object _lock = new();
    private readonly Queue<HardwareSample> _history = new();

    /// <summary>How often (in 1 Hz ticks) the GPU counter instance lists are re-enumerated.</summary>
    private const int GpuRefreshTicks = 5;

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

    public HardwareMonitor()
    {
        TryInit(ref _cpu, "Processor", "% Processor Time", "_Total");
        TryInit(ref _diskRead, "PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        TryInit(ref _diskWrite, "PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        RefreshGpuInstances();
        RefreshGpuMemoryInstances();
    }

    public void Start()
    {
        _timer = new Timer(_ => Sample(), null, 500, 1000);
        _procs.Start();
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

    /// <summary>
    /// Overall GPU utilisation with Task Manager semantics: sum the per-process values within
    /// each (adapter, engine-type) group, then report the busiest group, clamped to 0..100.
    /// Summing every instance instead would count each engine and each adapter (including
    /// virtual ones such as remote-desktop encoders) on top of one another and read near 100%
    /// on an idle machine.
    /// </summary>
    private double SampleGpuUtilization()
    {
        var groups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (inst, counter) in _gpu)
        {
            double v = SafeNext(counter);
            if (v <= 0) continue;
            string key = GpuGroupKey(inst);
            groups[key] = groups.TryGetValue(key, out double sum) ? sum + v : v;
        }

        double busiest = 0;
        foreach (double g in groups.Values) busiest = Math.Max(busiest, g);
        return Math.Clamp(busiest, 0, 100);
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
            }

            double cpu = SafeNext(_cpu);
            double dr = SafeNext(_diskRead);
            double dw = SafeNext(_diskWrite);
            double gpu = SampleGpuUtilization();

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
                _history.Enqueue(new HardwareSample
                {
                    Time = DateTimeOffset.UtcNow,
                    CpuPercent = cpu,
                    MemoryPercent = memPct,
                    DiskBytesPerSec = dr + dw,
                    GpuPercent = gpu,
                });
                while (_history.Count > MaxHistory) _history.Dequeue();
            }
        }
        catch { /* transient counter error */ }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private static double SafeNext(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; }
        catch { return 0; }
    }

    public HardwareSnapshot GetSnapshot()
    {
        lock (_lock)
        {
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
                History = _history.ToList(),
                Processes = _procs.Snapshot(),
            };
        }
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
        _timer?.Dispose();
        _procs.Dispose();
        _cpu?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        foreach (var c in _gpu.Values) c.Dispose();
        foreach (var c in _gpuMem.Values) c.Dispose();
    }
}
