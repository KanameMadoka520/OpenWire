using System.Collections.Concurrent;
using MaxMind.Db;
using MaxMind.GeoIP2;
using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Maps remote IPs to a country via a MaxMind-format IP-to-country database (DB-IP Lite or
/// GeoLite2). Degrades gracefully to <see cref="GeoInfo.Unknown"/> when no database is installed.
/// A single <see cref="DatabaseReader"/> is reused (construction is expensive, reads are
/// thread-safe) and results are cached per-IP. The reader is opened in <see cref="FileAccessMode.Memory"/>
/// so the database file is not locked on disk — that lets an in-app update replace it and call
/// <see cref="Reinitialize"/> to hot-swap without a restart.
/// </summary>
public sealed class GeoIpResolver : IDisposable
{
    private readonly string?[] _candidatePaths;
    private readonly object _sync = new();
    private DatabaseReader? _reader;
    private readonly ConcurrentDictionary<string, GeoInfo> _cache = new();

    public bool Available => _reader is not null;

    /// <summary>Path of the currently-open database, or null when none loaded.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Opens the first readable IP-to-country MMDB from the candidate paths
    /// (a downloaded/user-supplied database wins; a bundled one is the fallback).</summary>
    public GeoIpResolver(params string?[] candidatePaths)
    {
        _candidatePaths = candidatePaths;
        Open();
    }

    /// <summary>Re-open the best available candidate, replacing any current reader. Called after an
    /// update swaps the database file on disk.</summary>
    public void Reinitialize() => Open();

    private void Open()
    {
        DatabaseReader? opened = null; string? openedPath = null;
        foreach (var path in _candidatePaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try { opened = new DatabaseReader(path, FileAccessMode.Memory); openedPath = path; break; }
            catch (Exception ex) { Console.Error.WriteLine($"[GeoIP] failed to open '{path}': {ex.Message}"); }
        }
        DatabaseReader? old;
        lock (_sync) { old = _reader; _reader = opened; CurrentPath = openedPath; }
        _cache.Clear();
        // Memory-mode readers hold a private byte[] copy, so a lookup that raced this swap keeps
        // reading its captured reader safely; dispose the old one to free that copy.
        old?.Dispose();
    }

    /// <summary>Build date of the active database, from its metadata, or null.</summary>
    public DateTime? BuildDate
    {
        get { var r = _reader; try { return r?.Metadata.BuildDate; } catch { return null; } }
    }

    /// <summary>Raw metadata database type of the active database (e.g. "DBIP-Country-Lite").</summary>
    public string DatabaseType
    {
        get { var r = _reader; try { return r?.Metadata.DatabaseType ?? ""; } catch { return ""; } }
    }

    // One entry per unique remote IP with no expiry would grow forever on
    // endpoint-churn-heavy machines; DB lookups are cheap, so just reset wholesale.
    private const int MaxEntries = 20_000;

    public GeoInfo Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return GeoInfo.Unknown;
        if (ConnectionEnumerator.IsLocalAddress(ip)) return GeoInfo.Local;

        var reader = _reader; // snapshot: a concurrent Reinitialize may replace it
        if (reader is null) return GeoInfo.Unknown;

        if (_cache.Count >= MaxEntries) _cache.Clear();

        return _cache.GetOrAdd(ip, addr =>
        {
            try
            {
                var resp = reader.Country(addr);
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
