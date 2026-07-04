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

    private PerformanceCounter? _cpu;
    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private readonly List<PerformanceCounter> _gpu = new();

    private double _cpuV, _memPct, _diskR, _diskW, _gpuV;
    private long _memUsed, _memTotal;
    private Timer? _timer;

    public HardwareMonitor()
    {
        TryInit(ref _cpu, "Processor", "% Processor Time", "_Total");
        TryInit(ref _diskRead, "PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        TryInit(ref _diskWrite, "PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        InitGpu();
    }

    public void Start() => _timer = new Timer(_ => Sample(), null, 500, 1000);

    private static void TryInit(ref PerformanceCounter? counter, string cat, string name, string inst)
    {
        try { counter = new PerformanceCounter(cat, name, inst); counter.NextValue(); }
        catch { counter = null; }
    }

    private void InitGpu()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
                if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    c.NextValue();
                    _gpu.Add(c);
                }
                catch { /* skip this instance */ }
            }
        }
        catch { /* no GPU counters */ }
    }

    private void Sample()
    {
        try
        {
            double cpu = SafeNext(_cpu);
            double dr = SafeNext(_diskRead);
            double dw = SafeNext(_diskWrite);
            double gpu = 0;
            foreach (var c in _gpu) gpu += SafeNext(c);

            GetMemory(out long used, out long total);
            double memPct = total > 0 ? used * 100.0 / total : 0;

            lock (_lock)
            {
                _cpuV = cpu; _diskR = dr; _diskW = dw; _gpuV = gpu;
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
                History = _history.ToList(),
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
        _cpu?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        foreach (var c in _gpu) c.Dispose();
    }
}
