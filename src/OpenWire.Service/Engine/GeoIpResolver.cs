using System.Collections.Concurrent;
using MaxMind.GeoIP2;
using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Maps remote IPs to a country via a MaxMind GeoLite2 database. Degrades
/// gracefully to <see cref="GeoInfo.Unknown"/> when no database is installed.
/// A single <see cref="DatabaseReader"/> is reused (construction is expensive,
/// reads are thread-safe) and results are cached per-IP.
/// </summary>
public sealed class GeoIpResolver : IDisposable
{
    private readonly DatabaseReader? _reader;
    private readonly ConcurrentDictionary<string, GeoInfo> _cache = new();

    public bool Available => _reader is not null;

    /// <summary>Opens the first readable IP-to-country MMDB from the candidate paths
    /// (a user-supplied database wins; a bundled one is the fallback).</summary>
    public GeoIpResolver(params string?[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try { _reader = new DatabaseReader(path); return; }
            catch (Exception ex) { Console.Error.WriteLine($"[GeoIP] failed to open '{path}': {ex.Message}"); }
        }
    }

    // One entry per unique remote IP with no expiry would grow forever on
    // endpoint-churn-heavy machines; DB lookups are cheap, so just reset wholesale.
    private const int MaxEntries = 20_000;

    public GeoInfo Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return GeoInfo.Unknown;
        if (ConnectionEnumerator.IsLocalAddress(ip)) return GeoInfo.Local;
        if (_reader is null) return GeoInfo.Unknown;

        if (_cache.Count >= MaxEntries) _cache.Clear();

        return _cache.GetOrAdd(ip, addr =>
        {
            try
            {
                var resp = _reader.Country(addr);
                return new GeoInfo
                {
                    CountryCode = resp.Country.IsoCode ?? string.Empty,
                    CountryName = resp.Country.Name ?? string.Empty,
                };
            }
            catch
            {
                return GeoInfo.Unknown;
            }
        });
    }

    public void Dispose() => _reader?.Dispose();
}
