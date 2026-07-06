using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenWire.Core.Models;
using OpenWire.Service.Native;

namespace OpenWire.Service.Engine;

/// <summary>
/// Samples per-process CPU / memory / disk / GPU on a slow timer and keeps the
/// latest top-N rows for the Hardware screen's process table (GlassWire-style).
/// CPU and disk are rate metrics, so they're computed from deltas between ticks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessResourceMonitor : IDisposable
{
    private const int SampleMs = 2000;   // process enumeration is costly; 2 s is plenty
    private const int TopN = 40;

    private readonly int _cores = Environment.ProcessorCount;
    private readonly object _lock = new();
    private readonly Dictionary<int, Prev> _prev = new();
    private List<ProcessResourceRow> _latest = new();
    private Timer? _timer;
    private int _sampling;

    private readonly record struct Prev(long CpuTicks, ulong IoBytes, long Stamp);

    public void Start() => _timer = new Timer(_ => Sample(), null, 1000, SampleMs);

    public List<ProcessResourceRow> Snapshot()
    {
        lock (_lock) return _latest;
    }

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            var gpuByPid = SampleGpuByPid();
            var rows = new List<ProcessResourceRow>(256);
            var seen = new HashSet<int>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    int pid = p.Id;
                    if (pid <= 0) continue;
                    seen.Add(pid);

                    long cpuTicks = p.TotalProcessorTime.Ticks;
                    ulong io = ReadIoBytes(p);
                    long mem = p.WorkingSet64;

                    double cpuPct = 0, diskRate = 0;
                    if (_prev.TryGetValue(pid, out var prev) && prev.Stamp > 0)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(prev.Stamp, now).TotalSeconds;
                        if (elapsed > 0.05)
                        {
                            double cpuSec = (cpuTicks - prev.CpuTicks) / (double)TimeSpan.TicksPerSecond;
                            cpuPct = Math.Clamp(cpuSec / (elapsed * _cores) * 100.0, 0, 100);
                            if (io >= prev.IoBytes) diskRate = (io - prev.IoBytes) / elapsed;
                        }
                    }
                    _prev[pid] = new Prev(cpuTicks, io, now);

                    gpuByPid.TryGetValue(pid, out double gpu);

                    rows.Add(new ProcessResourceRow
                    {
                        Pid = pid,
                        Name = p.ProcessName,
                        ExecutablePath = TryPath(pid),
                        CpuPercent = cpuPct,
                        GpuPercent = gpu,
                        MemoryBytes = mem,
                        DiskBytesPerSec = diskRate,
                    });
                }
                catch { /* process exited mid-enumeration / access denied */ }
                finally { p.Dispose(); }
            }

            // Forget exited processes so the delta map stays bounded.
            foreach (var pid in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
                _prev.Remove(pid);

            // Rank by the resource that matters most, then keep the top slice.
            var top = rows
                .OrderByDescending(r => r.CpuPercent)
                .ThenByDescending(r => r.MemoryBytes)
                .Take(TopN)
                .ToList();

            lock (_lock) _latest = top;
        }
        catch { /* transient */ }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    /// <summary>Per-PID GPU utilisation from "GPU Engine" counters: for each PID,
    /// the max over its engine types (matches how the total GPU figure is computed).</summary>
    private static Dictionary<int, double> SampleGpuByPid()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
                int pid = ParsePid(inst);
                if (pid < 0) continue;
                double v;
                try
                {
                    using var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    c.NextValue();       // first read primes the counter; single-shot is ~0, acceptable
                    v = c.NextValue();
                }
                catch { continue; }
                // Sum within a (pid, engtype) is approximated by taking the max engtype per pid.
                if (!result.TryGetValue(pid, out double cur) || v > cur) result[pid] = Math.Clamp(v, 0, 100);
            }
        }
        catch { /* no GPU counters */ }
        return result;
    }

    private static int ParsePid(string instance)
    {
        // "pid_1234_luid_0x..._engtype_3D"
        const string pre = "pid_";
        int i = instance.IndexOf(pre, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        i += pre.Length;
        int j = i;
        while (j < instance.Length && char.IsDigit(instance[j])) j++;
        return int.TryParse(instance.AsSpan(i, j - i), out int pid) ? pid : -1;
    }

    private static string TryPath(int pid) => ProcessNative.GetProcessImagePath(pid) ?? "";

    private static ulong ReadIoBytes(Process p)
    {
        try
        {
            if (GetProcessIoCounters(p.Handle, out var io))
                return io.ReadTransferCount + io.WriteTransferCount;
        }
        catch { /* access denied for protected processes */ }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    public void Dispose() => _timer?.Dispose();
}
