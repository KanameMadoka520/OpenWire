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

    public GeoIpResolver(string? databasePath)
    {
        if (!string.IsNullOrEmpty(databasePath) && File.Exists(databasePath))
        {
            try { _reader = new DatabaseReader(databasePath); }
            catch (Exception ex) { Console.Error.WriteLine($"[GeoIP] failed to open '{databasePath}': {ex.Message}"); }
        }
    }

    public GeoInfo Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return GeoInfo.Unknown;
        if (ConnectionEnumerator.IsLocalAddress(ip)) return GeoInfo.Local;
        if (_reader is null) return GeoInfo.Unknown;

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
