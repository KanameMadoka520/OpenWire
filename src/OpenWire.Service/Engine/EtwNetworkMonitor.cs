using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace OpenWire.Service.Engine;

/// <summary>Mutable in/out byte counters updated from the ETW hot path.</summary>
public sealed class ProcCounter
{
    public long In;
    public long Out;
}

/// <summary>
/// Captures per-process network byte counts from the Windows kernel via ETW
/// (the <c>Microsoft-Windows-Kernel-Network</c> provider). Requires elevation.
/// Accumulates cumulative counters that the aggregator diffs once per second.
/// </summary>
public sealed class EtwNetworkMonitor : IDisposable
{
    private const string SessionName = "OpenWireKernelNet";
    private const int MaxEndpoints = 20_000;

    private readonly ConcurrentDictionary<int, ProcCounter> _perPid = new();
    private readonly ConcurrentDictionary<EndpointKey, ProcCounter> _perEndpoint = new();
    private readonly ConcurrentDictionary<string, ProcCounter> _perPortClass = new();

    private long _globalIn;
    private long _globalOut;
    private long _wanBytes;
    private long _lanBytes;

    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;

    public bool IsRunning => _running;

    public readonly record struct EndpointKey(int Pid, string Remote);

    /// <summary>Attempt to start the kernel ETW session. Returns false if unavailable.</summary>
    public bool TryStart()
    {
        if (_running) return true;
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = _session.Source.Kernel;
            kernel.TcpIpRecv += d => Record(d.ProcessID, d.size, incoming: true, d.saddr, d.sport);
            kernel.TcpIpSend += d => Record(d.ProcessID, d.size, incoming: false, d.daddr, d.dport);
            kernel.TcpIpRecvIPV6 += d => Record(d.ProcessID, d.size, incoming: true, d.saddr, d.sport);
            kernel.TcpIpSendIPV6 += d => Record(d.ProcessID, d.size, incoming: false, d.daddr, d.dport);
            kernel.UdpIpRecv += d => Record(d.ProcessID, d.size, incoming: true, d.saddr, d.sport);
            kernel.UdpIpSend += d => Record(d.ProcessID, d.size, incoming: false, d.daddr, d.dport);
            kernel.UdpIpRecvIPV6 += d => Record(d.ProcessID, d.size, incoming: true, d.saddr, d.sport);
            kernel.UdpIpSendIPV6 += d => Record(d.ProcessID, d.size, incoming: false, d.daddr, d.dport);

            _running = true;
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "OpenWire-ETW",
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();
            return true;
        }
        catch (Exception ex)
        {
            _running = false;
            _session?.Dispose();
            _session = null;
            Console.Error.WriteLine($"[ETW] kernel session unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void PumpLoop()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ETW] pump stopped: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private void Record(int pid, int size, bool incoming, IPAddress remote, int remotePort)
    {
        if (size <= 0) return;

        if (incoming) Interlocked.Add(ref _globalIn, size);
        else Interlocked.Add(ref _globalOut, size);

        bool local = remote is not null && ConnectionEnumerator.IsLocalAddress(remote);
        if (remote is not null)
        {
            if (local) Interlocked.Add(ref _lanBytes, size);
            else Interlocked.Add(ref _wanBytes, size);
        }

        if (!local && remotePort > 0)
        {
            var pc = _perPortClass.GetOrAdd(TrafficClassifier.Classify(remotePort), static _ => new ProcCounter());
            if (incoming) Interlocked.Add(ref pc.In, size);
            else Interlocked.Add(ref pc.Out, size);
        }

        if (pid > 0)
        {
            var pc = _perPid.GetOrAdd(pid, static _ => new ProcCounter());
            if (incoming) Interlocked.Add(ref pc.In, size);
            else Interlocked.Add(ref pc.Out, size);

            if (remote is not null)
            {
                var key = new EndpointKey(pid, remote.ToString());
                if (_perEndpoint.TryGetValue(key, out var ec))
                {
                    if (incoming) Interlocked.Add(ref ec.In, size);
                    else Interlocked.Add(ref ec.Out, size);
                }
                else if (_perEndpoint.Count < MaxEndpoints)
                {
                    ec = _perEndpoint.GetOrAdd(key, static _ => new ProcCounter());
                    if (incoming) Interlocked.Add(ref ec.In, size);
                    else Interlocked.Add(ref ec.Out, size);
                }
            }
        }
    }

    // ---- Cumulative reads for the aggregator (it computes per-second deltas) ----

    public (long In, long Out) ReadGlobal()
        => (Interlocked.Read(ref _globalIn), Interlocked.Read(ref _globalOut));

    public (long Wan, long Lan) ReadWanLan()
        => (Interlocked.Read(ref _wanBytes), Interlocked.Read(ref _lanBytes));

    public List<(string Name, long In, long Out)> SnapshotPortClasses()
    {
        var result = new List<(string, long, long)>(_perPortClass.Count);
        foreach (var kv in _perPortClass)
            result.Add((kv.Key, Interlocked.Read(ref kv.Value.In), Interlocked.Read(ref kv.Value.Out)));
        return result;
    }

    public Dictionary<int, (long In, long Out)> SnapshotPerPid()
    {
        var result = new Dictionary<int, (long, long)>(_perPid.Count);
        foreach (var kv in _perPid)
            result[kv.Key] = (Interlocked.Read(ref kv.Value.In), Interlocked.Read(ref kv.Value.Out));
        return result;
    }

    public List<(int Pid, string Remote, long In, long Out)> SnapshotEndpoints()
    {
        var result = new List<(int, string, long, long)>(_perEndpoint.Count);
        foreach (var kv in _perEndpoint)
            result.Add((kv.Key.Pid, kv.Key.Remote, Interlocked.Read(ref kv.Value.In), Interlocked.Read(ref kv.Value.Out)));
        return result;
    }

    /// <summary>
    /// Drop per-PID and per-endpoint counters whose owning process is gone. On
    /// process-churn-heavy machines these otherwise grow without bound — and dead
    /// flows fill the endpoint cap, silently stopping per-host attribution.
    /// </summary>
    public void PrunePids(HashSet<int> alivePids)
    {
        foreach (var pid in _perPid.Keys)
            if (!alivePids.Contains(pid)) _perPid.TryRemove(pid, out _);
        foreach (var key in _perEndpoint.Keys)
            if (!alivePids.Contains(key.Pid)) _perEndpoint.TryRemove(key, out _);
    }

    public void Dispose()
    {
        _running = false;
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
    }
}
