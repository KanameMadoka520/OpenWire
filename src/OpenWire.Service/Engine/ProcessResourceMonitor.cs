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
    private volatile bool _disposed;
    private volatile bool _primeNext; // first sample after a resume just re-primes deltas

    // Start time stamps the delta baseline so a recycled PID (same number, new process) fails the
    // match and re-primes just its own row instead of reporting a bogus CPU/disk spike.
    private readonly record struct Prev(long CpuTicks, ulong IoBytes, long Stamp, DateTime Start);

    public void Start() => _timer = new Timer(_ => Sample(), null, 1000, SampleMs);

    /// <summary>Pause entirely when the UI isn't viewing the process table (its two costs — a full
    /// <c>Process.GetProcesses()</c> sweep and a per-instance GPU counter construction — are the
    /// dominant idle CPU). The kept <c>_prev</c> map lets rows resume with real deltas.</summary>
    public void SetUiActive(bool active)
    {
        if (active) _primeNext = true; // resume: the next sample's deltas are stale — re-prime, don't publish
        try { if (!_disposed) _timer?.Change(active ? SampleMs : Timeout.Infinite, active ? SampleMs : Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

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
                    DateTime start; try { start = p.StartTime; } catch { start = default; }

                    double cpuPct = 0, diskRate = 0;
                    // Only diff against a baseline for the SAME process (matching start time); a
                    // reused PID or a long idle gap (paused monitor) re-primes this row cleanly.
                    if (_prev.TryGetValue(pid, out var prev) && prev.Stamp > 0 && prev.Start == start)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(prev.Stamp, now).TotalSeconds;
                        if (elapsed is > 0.05 and < 30)
                        {
                            double cpuSec = (cpuTicks - prev.CpuTicks) / (double)TimeSpan.TicksPerSecond;
                            cpuPct = Math.Clamp(cpuSec / (elapsed * _cores) * 100.0, 0, 100);
                            if (io >= prev.IoBytes) diskRate = (io - prev.IoBytes) / elapsed;
                        }
                    }
                    _prev[pid] = new Prev(cpuTicks, io, now, start);

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

            // On the first sample after a resume every row re-primed (CPU 0), which would flash an
            // all-zero, memory-sorted table; keep the previous one for this interval instead.
            if (_primeNext) { _primeNext = false; return; }
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

    public void Dispose() { _disposed = true; _timer?.Dispose(); }
}
