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
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly SemaphoreSlim _gate = new(8);
    private volatile bool _enabled = true;

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

        if (_cache.TryGetValue(ip, out var e))
        {
            if (e.Host.Length > 0 && e.Expiry > DateTimeOffset.UtcNow) return e.Host;
            if (e.Pending) return e.Host; // may be empty while first lookup runs
        }

        QueueLookup(ip);
        return _cache.TryGetValue(ip, out var cur) ? cur.Host : string.Empty;
    }

    private void QueueLookup(string ip)
    {
        var entry = _cache.GetOrAdd(ip, _ => new Entry());
        lock (entry)
        {
            if (entry.Pending) return;
            if (entry.Host.Length > 0 && entry.Expiry > DateTimeOffset.UtcNow) return;
            entry.Pending = true;
        }

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
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
