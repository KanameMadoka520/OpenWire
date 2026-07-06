using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenWire.Core.Models;

namespace OpenWire.Service.Storage;

/// <summary>
/// SQLite-backed history + configuration store (WAL mode). Holds per-minute traffic
/// rollups (global, per-app, per-host), the alert log, the device inventory, app
/// metadata and settings. All access is serialized on a single connection.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    /// <summary>Absolute path of the database file backing this store.</summary>
    public string DbPath { get; }

    public HistoryStore(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;");
        CreateSchema();
    }

    /// <summary>Total bytes of the database (main file + WAL + SHM sidecars).</summary>
    public long DatabaseBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { var fi = new FileInfo(DbPath + suffix); if (fi.Exists) total += fi.Length; }
            catch { /* ignore */ }
        }
        return total;
    }

    /// <summary>Oldest retained per-minute record (UTC), or null when empty.</summary>
    public DateTimeOffset? OldestRecordUtc()
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(bucket) FROM traffic_min";
                var v = cmd.ExecuteScalar();
                if (v is long b && b > 0) return DateTimeOffset.FromUnixTimeSeconds(b);
            }
            catch { /* table may not exist yet */ }
            return null;
        }
    }

    /// <summary>Flush the WAL back into the main file so it can be safely copied.</summary>
    public void Checkpoint()
    {
        lock (_lock) { try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { } }
    }

    /// <summary>Delete recorded history (per <paramref name="mode"/>) and compact the file.</summary>
    public void ClearData(ClearDataMode mode)
    {
        lock (_lock)
        {
            if (mode == ClearDataMode.MinuteHistory)
            {
                Exec("DELETE FROM traffic_min; DELETE FROM usage_app; DELETE FROM usage_host;");
            }
            else // AllHistory
            {
                Exec(@"DELETE FROM traffic_min;  DELETE FROM usage_app;      DELETE FROM usage_host;
                       DELETE FROM traffic_day;  DELETE FROM usage_app_day;
                       DELETE FROM host_meta;    DELETE FROM country_seen;   DELETE FROM devices;");
            }
            try { Exec("VACUUM;"); } catch { /* vacuum can fail under WAL; harmless */ }
        }
    }

    private void CreateSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);

CREATE TABLE IF NOT EXISTS app_meta (
    app_id TEXT PRIMARY KEY, name TEXT, path TEXT, publisher TEXT, version TEXT, is_signed INTEGER);

CREATE TABLE IF NOT EXISTS traffic_min (
    bucket INTEGER PRIMARY KEY, bytes_in INTEGER NOT NULL, bytes_out INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS usage_app (
    app_id TEXT NOT NULL, bucket INTEGER NOT NULL,
    bytes_in INTEGER NOT NULL, bytes_out INTEGER NOT NULL,
    PRIMARY KEY (app_id, bucket));
CREATE INDEX IF NOT EXISTS ix_usage_app_bucket ON usage_app(bucket);

CREATE TABLE IF NOT EXISTS usage_host (
    host_key TEXT NOT NULL, bucket INTEGER NOT NULL,
    bytes_in INTEGER NOT NULL, bytes_out INTEGER NOT NULL,
    PRIMARY KEY (host_key, bucket));
CREATE INDEX IF NOT EXISTS ix_usage_host_bucket ON usage_host(bucket);

-- Compact per-local-day rollups. Kept far longer than the minute tables so
-- long-term trends and per-app anomaly baselines survive minute-row pruning.
CREATE TABLE IF NOT EXISTS traffic_day (
    day INTEGER PRIMARY KEY, bytes_in INTEGER NOT NULL, bytes_out INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS usage_app_day (
    app_id TEXT NOT NULL, day INTEGER NOT NULL,
    bytes_in INTEGER NOT NULL, bytes_out INTEGER NOT NULL,
    PRIMARY KEY (app_id, day));
CREATE INDEX IF NOT EXISTS ix_usage_app_day_day ON usage_app_day(day);

-- First/last time each country was ever contacted (survives pruning) so a first
-- contact with a new country can be flagged as an anomaly.
CREATE TABLE IF NOT EXISTS country_seen (
    code TEXT PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS host_meta (
    host_key TEXT PRIMARY KEY, remote_addr TEXT, host_name TEXT, country_code TEXT, country_name TEXT);

CREATE TABLE IF NOT EXISTS alerts (
    id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, kind INTEGER NOT NULL,
    severity INTEGER NOT NULL, title TEXT, message TEXT,
    app_id TEXT, app_name TEXT, device_id TEXT, remote_host TEXT, ack INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS ix_alerts_time ON alerts(time);

CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY, name TEXT, custom_name TEXT, ip TEXT, mac TEXT, vendor TEXT,
    kind INTEGER, is_gateway INTEGER, is_this INTEGER, first_seen INTEGER, last_seen INTEGER, online INTEGER,
    description TEXT, os TEXT);
");

        // Lightweight migrations for databases created by earlier versions.
        TryExec("ALTER TABLE devices ADD COLUMN description TEXT;");
        TryExec("ALTER TABLE devices ADD COLUMN os TEXT;");
    }

    private void TryExec(string sql)
    {
        try { Exec(sql); } catch { /* column already exists */ }
    }

    // ---------------- Settings ----------------

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            var json = GetKv("settings");
            if (string.IsNullOrEmpty(json)) return new AppSettings();
            try { return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings(); }
            catch { return new AppSettings(); }
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
            SetKv("settings", JsonSerializer.Serialize(settings));
    }

    // ---------------- Traffic rollups ----------------

    public void AddGlobalMinute(long bucket, long bytesIn, long bytesOut)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO traffic_min(bucket,bytes_in,bytes_out) VALUES($b,$i,$o)
                ON CONFLICT(bucket) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o;";
            Bind(cmd, "$b", bucket); Bind(cmd, "$i", bytesIn); Bind(cmd, "$o", bytesOut);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddAppMinute(string appId, long bucket, long bytesIn, long bytesOut)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO usage_app(app_id,bucket,bytes_in,bytes_out) VALUES($a,$b,$i,$o)
                ON CONFLICT(app_id,bucket) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o;";
            Bind(cmd, "$a", appId); Bind(cmd, "$b", bucket); Bind(cmd, "$i", bytesIn); Bind(cmd, "$o", bytesOut);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddHostMinute(string hostKey, long bucket, long bytesIn, long bytesOut)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO usage_host(host_key,bucket,bytes_in,bytes_out) VALUES($h,$b,$i,$o)
                ON CONFLICT(host_key,bucket) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o;";
            Bind(cmd, "$h", hostKey); Bind(cmd, "$b", bucket); Bind(cmd, "$i", bytesIn); Bind(cmd, "$o", bytesOut);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddGlobalDay(long day, long bytesIn, long bytesOut)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO traffic_day(day,bytes_in,bytes_out) VALUES($d,$i,$o)
                ON CONFLICT(day) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o;";
            Bind(cmd, "$d", day); Bind(cmd, "$i", bytesIn); Bind(cmd, "$o", bytesOut);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddAppDay(string appId, long day, long bytesIn, long bytesOut)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO usage_app_day(app_id,day,bytes_in,bytes_out) VALUES($a,$d,$i,$o)
                ON CONFLICT(app_id,day) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o;";
            Bind(cmd, "$a", appId); Bind(cmd, "$d", day); Bind(cmd, "$i", bytesIn); Bind(cmd, "$o", bytesOut);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Record that <paramref name="code"/> was contacted at <paramref name="nowUnix"/>.</summary>
    public void TouchCountry(string code, long nowUnix)
    {
        if (string.IsNullOrEmpty(code)) return;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO country_seen(code,first_seen,last_seen) VALUES($c,$n,$n)
                ON CONFLICT(code) DO UPDATE SET last_seen=$n;";
            Bind(cmd, "$c", code); Bind(cmd, "$n", nowUnix);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertAppMeta(AppInfo app)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO app_meta(app_id,name,path,publisher,version,is_signed)
                VALUES($a,$n,$p,$pub,$v,$s)
                ON CONFLICT(app_id) DO UPDATE SET name=$n, path=$p, publisher=$pub, version=$v, is_signed=$s;";
            Bind(cmd, "$a", app.Id); Bind(cmd, "$n", app.Name); Bind(cmd, "$p", app.ExecutablePath);
            Bind(cmd, "$pub", app.Publisher); Bind(cmd, "$v", app.Version); Bind(cmd, "$s", app.IsSigned ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertHostMeta(string hostKey, string remoteAddr, string hostName, GeoInfo geo)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO host_meta(host_key,remote_addr,host_name,country_code,country_name)
                VALUES($h,$r,$hn,$cc,$cn)
                ON CONFLICT(host_key) DO UPDATE SET remote_addr=$r,
                    host_name=CASE WHEN $hn<>'' THEN $hn ELSE host_name END,
                    country_code=CASE WHEN $cc<>'' THEN $cc ELSE country_code END,
                    country_name=CASE WHEN $cn<>'' THEN $cn ELSE country_name END;";
            Bind(cmd, "$h", hostKey); Bind(cmd, "$r", remoteAddr); Bind(cmd, "$hn", hostName);
            Bind(cmd, "$cc", geo.CountryCode); Bind(cmd, "$cn", geo.CountryName);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Per-minute global traffic between two minute buckets (inclusive).</summary>
    public List<(long Bucket, long In, long Out)> QueryGlobal(long fromBucket, long toBucket)
    {
        lock (_lock)
        {
            var list = new List<(long, long, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT bucket,bytes_in,bytes_out FROM traffic_min WHERE bucket>=$f AND bucket<=$t ORDER BY bucket;";
            Bind(cmd, "$f", fromBucket); Bind(cmd, "$t", toBucket);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
            return list;
        }
    }

    public List<AppUsage> QueryUsageByApp(long fromBucket, long toBucket)
    {
        lock (_lock)
        {
            var list = new List<AppUsage>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT u.app_id, COALESCE(m.name,u.app_id), COALESCE(m.path,''),
                    COALESCE(m.publisher,''), SUM(u.bytes_in), SUM(u.bytes_out)
                FROM usage_app u LEFT JOIN app_meta m ON m.app_id=u.app_id
                WHERE u.bucket>=$f AND u.bucket<=$t
                GROUP BY u.app_id ORDER BY SUM(u.bytes_in)+SUM(u.bytes_out) DESC;";
            Bind(cmd, "$f", fromBucket); Bind(cmd, "$t", toBucket);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AppUsage
                {
                    App = new AppInfo { Id = r.GetString(0), Name = r.GetString(1), ExecutablePath = r.GetString(2), Publisher = r.GetString(3) },
                    BytesIn = r.GetInt64(4),
                    BytesOut = r.GetInt64(5),
                });
            }
            return list;
        }
    }

    /// <summary>Per-application totals grouped by local hour-of-day (0..23) over a minute-bucket
    /// window — feeds the "which apps" hover breakdown on the hour-of-day chart.</summary>
    public List<(int Hour, string Name, string Path, long In, long Out)> QueryAppByHour(long fromBucket, long toBucket)
    {
        lock (_lock)
        {
            var list = new List<(int, string, string, long, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT CAST(strftime('%H', u.bucket, 'unixepoch', 'localtime') AS INTEGER) AS hr,
                    COALESCE(m.name,u.app_id), COALESCE(m.path,''), SUM(u.bytes_in), SUM(u.bytes_out)
                FROM usage_app u LEFT JOIN app_meta m ON m.app_id=u.app_id
                WHERE u.bucket>=$f AND u.bucket<=$t
                GROUP BY hr, u.app_id;";
            Bind(cmd, "$f", fromBucket); Bind(cmd, "$t", toBucket);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    /// <summary>Per-application totals grouped by local calendar day ("yyyy-MM-dd") over a
    /// minute-bucket window — feeds the "which apps" hover breakdown on the daily chart.</summary>
    public List<(string Day, string Name, string Path, long In, long Out)> QueryAppByDay(long fromBucket, long toBucket)
    {
        lock (_lock)
        {
            var list = new List<(string, string, string, long, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT strftime('%Y-%m-%d', u.bucket, 'unixepoch', 'localtime') AS day,
                    COALESCE(m.name,u.app_id), COALESCE(m.path,''), SUM(u.bytes_in), SUM(u.bytes_out)
                FROM usage_app u LEFT JOIN app_meta m ON m.app_id=u.app_id
                WHERE u.bucket>=$f AND u.bucket<=$t
                GROUP BY day, u.app_id;";
            Bind(cmd, "$f", fromBucket); Bind(cmd, "$t", toBucket);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    public List<HostUsage> QueryUsageByHost(long fromBucket, long toBucket)
    {
        lock (_lock)
        {
            var list = new List<HostUsage>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(NULLIF(h.host_name,''),h.remote_addr,u.host_key), COALESCE(h.remote_addr,u.host_key),
                    COALESCE(h.country_code,''), COALESCE(h.country_name,''),
                    SUM(u.bytes_in), SUM(u.bytes_out)
                FROM usage_host u LEFT JOIN host_meta h ON h.host_key=u.host_key
                WHERE u.bucket>=$f AND u.bucket<=$t
                GROUP BY u.host_key ORDER BY SUM(u.bytes_in)+SUM(u.bytes_out) DESC LIMIT 500;";
            Bind(cmd, "$f", fromBucket); Bind(cmd, "$t", toBucket);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HostUsage
                {
                    Host = r.GetString(0),
                    RemoteAddress = r.GetString(1),
                    Geo = new GeoInfo { CountryCode = r.GetString(2), CountryName = r.GetString(3) },
                    BytesIn = r.GetInt64(4),
                    BytesOut = r.GetInt64(5),
                });
            }
            return list;
        }
    }

    public long DataPlanUsed(long sinceBucket)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(bytes_in+bytes_out),0) FROM traffic_min WHERE bucket>=$s;";
            Bind(cmd, "$s", sinceBucket);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public (long In, long Out) TotalTraffic()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(bytes_in),0), COALESCE(SUM(bytes_out),0) FROM traffic_min;";
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        }
    }

    // ---------------- Analytics + retention ----------------

    /// <summary>Per-local-day global totals between two day buckets (inclusive).</summary>
    public List<(long Day, long In, long Out)> QueryGlobalDaily(long fromDay, long toDay)
    {
        lock (_lock)
        {
            var list = new List<(long, long, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT day,bytes_in,bytes_out FROM traffic_day WHERE day>=$f AND day<=$t ORDER BY day;";
            Bind(cmd, "$f", fromDay); Bind(cmd, "$t", toDay);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
            return list;
        }
    }

    /// <summary>
    /// Per-app daily baseline over [fromDay, toDay): active-day count and byte totals,
    /// used to compute an "average per active day" baseline for spike detection.
    /// </summary>
    public List<(string AppId, int ActiveDays, long In, long Out)> AppDailyBaseline(long fromDay, long toDay)
    {
        lock (_lock)
        {
            var list = new List<(string, int, long, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT app_id, COUNT(*), SUM(bytes_in), SUM(bytes_out)
                FROM usage_app_day WHERE day>=$f AND day<$t GROUP BY app_id;";
            Bind(cmd, "$f", fromDay); Bind(cmd, "$t", toDay);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)));
            return list;
        }
    }

    /// <summary>Countries whose very first contact happened at or after <paramref name="cutoffUnix"/>.</summary>
    public List<(string Code, long FirstSeen)> NewCountriesSince(long cutoffUnix)
    {
        lock (_lock)
        {
            var list = new List<(string, long)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT code,first_seen FROM country_seen WHERE first_seen>=$c AND code<>'' ORDER BY first_seen DESC;";
            Bind(cmd, "$c", cutoffUnix);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
            return list;
        }
    }

    /// <summary>Resolve a country code to its friendly name from recorded host metadata.</summary>
    public string CountryName(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT country_name FROM host_meta WHERE country_code=$c AND country_name<>'' LIMIT 1;";
            Bind(cmd, "$c", code);
            return cmd.ExecuteScalar() as string ?? code;
        }
    }

    /// <summary>
    /// Drop minute-granularity rows older than <paramref name="minuteCutoffUnix"/> and
    /// day rollups older than <paramref name="dayCutoffUnix"/>. Keeps the DB bounded
    /// while preserving long-term daily trends. Returns rows removed.
    /// </summary>
    public int PruneOldHistory(long minuteCutoffUnix, long dayCutoffUnix)
    {
        lock (_lock)
        {
            int removed = 0;
            removed += ExecDelete("DELETE FROM traffic_min WHERE bucket<$c;", minuteCutoffUnix);
            removed += ExecDelete("DELETE FROM usage_app  WHERE bucket<$c;", minuteCutoffUnix);
            removed += ExecDelete("DELETE FROM usage_host WHERE bucket<$c;", minuteCutoffUnix);
            removed += ExecDelete("DELETE FROM traffic_day    WHERE day<$c;", dayCutoffUnix);
            removed += ExecDelete("DELETE FROM usage_app_day  WHERE day<$c;", dayCutoffUnix);

            // Drop host metadata for hosts that no longer have any usage rows, so the
            // table doesn't accumulate a permanent row per IP ever contacted.
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM host_meta WHERE host_key NOT IN (SELECT DISTINCT host_key FROM usage_host);";
                removed += cmd.ExecuteNonQuery();
            }
            return removed;
        }
    }

    private int ExecDelete(string sql, long cutoff)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, "$c", cutoff);
        return cmd.ExecuteNonQuery();
    }

    // ---------------- Alerts ----------------

    public long InsertAlert(Alert a)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO alerts(time,kind,severity,title,message,app_id,app_name,device_id,remote_host,ack)
                VALUES($t,$k,$s,$ti,$m,$a,$an,$d,$rh,0); SELECT last_insert_rowid();";
            Bind(cmd, "$t", a.Time.ToUnixTimeSeconds()); Bind(cmd, "$k", (int)a.Kind); Bind(cmd, "$s", (int)a.Severity);
            Bind(cmd, "$ti", a.Title); Bind(cmd, "$m", a.Message);
            Bind(cmd, "$a", (object?)a.AppId ?? DBNull.Value); Bind(cmd, "$an", (object?)a.AppName ?? DBNull.Value);
            Bind(cmd, "$d", (object?)a.DeviceId ?? DBNull.Value); Bind(cmd, "$rh", (object?)a.RemoteHost ?? DBNull.Value);
            a.Id = Convert.ToInt64(cmd.ExecuteScalar());
            return a.Id;
        }
    }

    public List<Alert> GetAlerts(int limit)
    {
        lock (_lock)
        {
            var list = new List<Alert>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id,time,kind,severity,title,message,app_id,app_name,device_id,remote_host,ack FROM alerts ORDER BY id DESC LIMIT $l;";
            Bind(cmd, "$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Alert
                {
                    Id = r.GetInt64(0),
                    Time = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                    Kind = (AlertKind)r.GetInt32(2),
                    Severity = (AlertSeverity)r.GetInt32(3),
                    Title = r.GetString(4),
                    Message = r.GetString(5),
                    AppId = r.IsDBNull(6) ? null : r.GetString(6),
                    AppName = r.IsDBNull(7) ? null : r.GetString(7),
                    DeviceId = r.IsDBNull(8) ? null : r.GetString(8),
                    RemoteHost = r.IsDBNull(9) ? null : r.GetString(9),
                    Acknowledged = r.GetInt32(10) != 0,
                });
            }
            return list;
        }
    }

    public int UnreadAlertCount()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM alerts WHERE ack=0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void AckAlert(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE alerts SET ack=1 WHERE id=$i;";
            Bind(cmd, "$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void AckAllAlerts()
    {
        lock (_lock) Exec("UPDATE alerts SET ack=1 WHERE ack=0;");
    }

    public List<string> GetKnownAppIds()
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT app_id FROM app_meta;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    // ---------------- Devices ----------------

    public void UpsertDevice(Device d)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO devices(id,name,ip,mac,vendor,kind,is_gateway,is_this,first_seen,last_seen,online,description,os)
                VALUES($id,$n,$ip,$mac,$v,$k,$g,$t,$fs,$ls,$on,$desc,$os)
                ON CONFLICT(id) DO UPDATE SET name=$n, ip=$ip, vendor=$v, kind=$k,
                    is_gateway=$g, last_seen=$ls, online=$on, description=$desc, os=$os;";
            Bind(cmd, "$id", d.Id); Bind(cmd, "$n", d.Name); Bind(cmd, "$ip", d.IpAddress); Bind(cmd, "$mac", d.MacAddress);
            Bind(cmd, "$v", d.Vendor); Bind(cmd, "$k", (int)d.Kind); Bind(cmd, "$g", d.IsGateway ? 1 : 0);
            Bind(cmd, "$t", d.IsThisDevice ? 1 : 0);
            Bind(cmd, "$fs", d.FirstSeen.ToUnixTimeSeconds()); Bind(cmd, "$ls", d.LastSeen.ToUnixTimeSeconds());
            Bind(cmd, "$on", d.IsOnline ? 1 : 0);
            Bind(cmd, "$desc", d.Description); Bind(cmd, "$os", d.OperatingSystem);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDevicesOffline(IEnumerable<string> onlineIds)
    {
        lock (_lock)
        {
            var set = onlineIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM devices WHERE online=1;";
            var toOffline = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!set.Contains(id)) toOffline.Add(id);
                }
            foreach (var id in toOffline)
            {
                using var up = _conn.CreateCommand();
                up.CommandText = "UPDATE devices SET online=0 WHERE id=$i;";
                Bind(up, "$i", id);
                up.ExecuteNonQuery();
            }
        }
    }

    public List<Device> GetDevices()
    {
        lock (_lock)
        {
            var list = new List<Device>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT id,COALESCE(NULLIF(custom_name,''),name),ip,mac,vendor,kind,is_gateway,is_this,first_seen,last_seen,online,
                COALESCE(description,''),COALESCE(os,'')
                FROM devices ORDER BY online DESC, is_this DESC, last_seen DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Device
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    IpAddress = r.GetString(2),
                    MacAddress = r.GetString(3),
                    Vendor = r.GetString(4),
                    Kind = (DeviceKind)r.GetInt32(5),
                    IsGateway = r.GetInt32(6) != 0,
                    IsThisDevice = r.GetInt32(7) != 0,
                    FirstSeen = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)),
                    LastSeen = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(9)),
                    IsOnline = r.GetInt32(10) != 0,
                    Description = r.GetString(11),
                    OperatingSystem = r.GetString(12),
                });
            }
            return list;
        }
    }

    public List<string> GetKnownDeviceIds()
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM devices;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void RenameDevice(string id, string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE devices SET custom_name=$n WHERE id=$i;";
            Bind(cmd, "$n", name); Bind(cmd, "$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ForgetDevice(string id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM devices WHERE id=$i;";
            Bind(cmd, "$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- helpers ----------------

    private string? GetKv(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k;";
        Bind(cmd, "$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetKv(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
        Bind(cmd, "$k", key); Bind(cmd, "$v", value);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string name, object value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* ignore */ }
            _conn.Dispose();
        }
    }
}
