using System.IO.Compression;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;

namespace OpenWire.Service.Engine;

/// <summary>
/// Downloads the latest free DB-IP Lite country database and installs it into the data directory,
/// so GeoIP attribution stays current without shipping a new OpenWire build. The database is
/// decoupled from the app: a downloaded file in the (writable) data dir takes priority over the
/// bundled one, so even an old OpenWire keeps a fresh database.
///
/// Source: DB-IP Lite IP-to-Country, published monthly, free (CC-BY 4.0), no API key. See the
/// bundled Assets/NOTICE.md for attribution.
/// </summary>
public sealed class GeoIpUpdater
{
    private const string UrlFormat = "https://download.db-ip.com/free/dbip-country-lite-{0:yyyy-MM}.mmdb.gz";
    private const long MinDbBytes = 100_000;               // a country DB is a few MB; reject truncation
    private const long MaxDbBytes = 512L * 1024 * 1024;    // ...and reject anything absurdly large

    // Default handler → uses the system proxy, which matters on locked-down networks.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None, // the body is a .gz file, we gunzip it ourselves
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _dataDir;
    private readonly GeoIpResolver _resolver;

    public GeoIpUpdater(string dataDir, GeoIpResolver resolver)
    {
        _dataDir = dataDir;
        _resolver = resolver;
    }

    /// <summary>Where a downloaded database is installed (the resolver's top-priority candidate).</summary>
    public string InstalledPath => Path.Combine(_dataDir, "geoip-country.mmdb");

    public sealed record Result(bool Success, bool Updated, string Message, DateTime? BuildDate);

    /// <summary>
    /// Fetch the newest monthly DB-IP Lite country database and install it when it is newer than
    /// the currently-active database (or when none is installed). Tries the current month, then
    /// falls back to the previous month if it isn't published yet. The download is validated
    /// (opened + a known IP resolved) before it replaces anything; the existing database survives
    /// any failure.
    /// </summary>
    public async Task<Result> UpdateAsync(DateTime utcNow, DateTime? currentBuildDate, CancellationToken ct)
    {
        string? lastError = null;
        for (int monthsBack = 0; monthsBack <= 1; monthsBack++)
        {
            var url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                UrlFormat, utcNow.AddMonths(-monthsBack));
            try
            {
                return await TryOneAsync(url, currentBuildDate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex.Message; }
        }
        return new Result(false, false, lastError ?? "download failed", null);
    }

    private async Task<Result> TryOneAsync(string url, DateTime? currentBuildDate, CancellationToken ct)
    {
        string tmpGz = InstalledPath + ".gz.tmp";
        string tmpDb = InstalledPath + ".tmp";
        try
        {
            // 1) download the gzip to a temp file
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = File.Create(tmpGz);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // 2) decompress -> temp mmdb
            await using (var gzIn = File.OpenRead(tmpGz))
            await using (var gz = new GZipStream(gzIn, CompressionMode.Decompress))
            await using (var outFs = File.Create(tmpDb))
                await gz.CopyToAsync(outFs, ct).ConfigureAwait(false);

            long size = new FileInfo(tmpDb).Length;
            if (size is < MinDbBytes or > MaxDbBytes)
                return new Result(false, false, $"unexpected database size ({size:N0} bytes)", null);

            // 3) validate: open it and resolve a known public IP; read the build date
            DateTime buildDate;
            using (var test = new DatabaseReader(tmpDb, FileAccessMode.Memory))
            {
                _ = test.Country("8.8.8.8"); // throws if the file isn't a usable country database
                buildDate = test.Metadata.BuildDate;
            }

            // 4) don't replace a same-or-newer database with an older/equal one
            if (currentBuildDate is { } cur && buildDate <= cur)
                return new Result(true, false, "already up to date", cur);

            // 5) install: Memory-mode readers don't lock the file, so overwrite + hot-swap
            File.Move(tmpDb, InstalledPath, overwrite: true);
            _resolver.Reinitialize();
            return new Result(true, true, "updated", buildDate);
        }
        finally
        {
            TryDelete(tmpGz);
            TryDelete(tmpDb); // no-op after a successful File.Move
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
