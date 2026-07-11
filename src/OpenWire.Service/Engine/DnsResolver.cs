using System.Collections.Concurrent;
using System.Net;

namespace OpenWire.Service.Engine;

/// <summary>
/// Non-blocking reverse-DNS resolver. Callers ask for a name synchronously and
/// get whatever is cached; unknown IPs are resolved in the background with bounded
/// concurrency and a TTL, so hot paths never block on PTR lookups.
/// </summary>
public sealed class DnsResolver
{
    private sealed class Entry
    {
        public string Host = string.Empty;
        public DateTimeOffset Expiry;
        public bool Pending;
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SaturatedRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly SemaphoreSlim _gate = new(8);
    private volatile bool _enabled = true;
    private DateTimeOffset _nextSweep = DateTimeOffset.UtcNow + SweepEvery;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Returns the resolved host name for an IP if known, else an empty string,
    /// scheduling a background lookup on first request.
    /// </summary>
    public string Resolve(string ip)
    {
        if (!_enabled || string.IsNullOrEmpty(ip)) return string.Empty;
        if (ConnectionEnumerator.IsLocalAddress(ip)) return string.Empty;
        MaybeSweep();

        if (_cache.TryGetValue(ip, out var e))
        {
            // A valid entry (positive OR a negative/NXDOMAIN result within its TTL) is
            // returned as-is without scheduling another lookup.
            if (e.Expiry > DateTimeOffset.UtcNow) return e.Host;
            if (e.Pending) return e.Host; // may be empty while first lookup runs
        }

        QueueLookup(ip);
        return _cache.TryGetValue(ip, out var cur) ? cur.Host : string.Empty;
    }

    /// <summary>
    /// Drop entries that expired a full TTL ago and were never asked about again.
    /// Without this the cache grows one entry per unique remote IP for the life of
    /// the process — unbounded on proxy-heavy machines with high endpoint churn.
    /// </summary>
    private void MaybeSweep()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextSweep) return;
        _nextSweep = now + SweepEvery;
        foreach (var kv in _cache)
            if (!kv.Value.Pending && kv.Value.Expiry < now - Ttl)
                _cache.TryRemove(kv.Key, out _);
    }

    private void QueueLookup(string ip)
    {
        var entry = _cache.GetOrAdd(ip, _ => new Entry());
        lock (entry)
        {
            if (entry.Pending) return;
            if (entry.Expiry > DateTimeOffset.UtcNow) return; // still valid (positive or negative)
            entry.Pending = true;
        }

        // Do not create an unbounded Task/sem_waiter backlog when a burst contains thousands of
        // new endpoints. A saturated resolver records a short negative result and will retry on
        // a later observation, keeping the number of live DNS operations bounded by the gate.
        if (!_gate.Wait(0))
        {
            lock (entry)
            {
                entry.Pending = false;
                entry.Expiry = DateTimeOffset.UtcNow + SaturatedRetry;
            }
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string host = string.Empty;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var he = await Dns.GetHostEntryAsync(ip, cts.Token).ConfigureAwait(false);
                    if (!string.Equals(he.HostName, ip, StringComparison.Ordinal))
                        host = he.HostName;
                }
                catch
                {
                    // NXDOMAIN / timeout — leave empty, retry after TTL.
                }

                lock (entry)
                {
                    entry.Host = host;
                    entry.Expiry = DateTimeOffset.UtcNow + Ttl;
                    entry.Pending = false;
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }
}
