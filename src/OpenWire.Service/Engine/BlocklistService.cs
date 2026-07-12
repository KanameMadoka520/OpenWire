using System.Net;
using System.Text.Json;
using OpenWire.Core.Models;
using OpenWire.Service.Storage;

namespace OpenWire.Service.Engine;

/// <summary>
/// Downloads, caches and matches community host blocklists (hosts-file / plain domain / plain IP
/// formats). Lists are fetched only while enabled, cached in SQLite so restarts need no network,
/// and matched entirely in memory. The service is deliberately policy-free: the engine decides
/// whether a match becomes an alert, a firewall block, or both.
/// </summary>
public sealed class BlocklistService : IDisposable
{
    private const string MetaKey = "blocklist.meta";
    private const int KindDomain = 0;
    private const int KindIp = 1;
    private const int MaxEntriesPerList = 500_000;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    /// <summary>Hosts-file boilerplate names that must never be treated as blocked hosts.</summary>
    private static readonly HashSet<string> SkipHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
    };

    private sealed class MatchIndex
    {
        public static readonly MatchIndex Empty = new();
        public Dictionary<string, string> Domains { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Ips { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ListNames { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ListMeta
    {
        public long LastFetchUnix { get; set; }
        public int EntryCount { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    private readonly HistoryStore _store;
    private readonly HttpClient _http;
    private readonly object _lock = new();

    private List<BlocklistSubscription> _subs = new();
    private Dictionary<string, ListMeta> _meta;
    private volatile MatchIndex _index = MatchIndex.Empty;
    private int _refreshing;

    public BlocklistService(HistoryStore store)
    {
        _store = store;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 32 * 1024 * 1024,
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "OpenWire/0.1");
        _meta = LoadMeta();
    }

    public bool Refreshing => Volatile.Read(ref _refreshing) == 1;

    /// <summary>
    /// Push the current subscription set (called on startup and every settings save). Drops the
    /// cache of removed lists, rebuilds the in-memory index from the SQLite cache, and schedules
    /// a background download for enabled lists whose cache is missing or stale.
    /// </summary>
    public void Configure(IEnumerable<BlocklistSubscription> subscriptions)
    {
        lock (_lock)
        {
            var next = subscriptions
                .Select(s => new BlocklistSubscription
                {
                    Id = s.Id, Name = s.Name, Url = s.Url, Enabled = s.Enabled, IsPreset = s.IsPreset,
                })
                .ToList();

            var keep = next.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string removed in _meta.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                _store.DeleteBlocklistEntries(removed);
                _meta.Remove(removed);
            }

            _subs = next;
            SaveMeta();
        }

        _ = Task.Run(() =>
        {
            RebuildIndex();
            return RefreshAsync(listId: null, force: false);
        });
    }

    /// <summary>
    /// Download every enabled list whose cache is stale (or one list / all lists when forced).
    /// Concurrent calls collapse into the running pass.
    /// </summary>
    public async Task RefreshAsync(string? listId, bool force)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            List<BlocklistSubscription> targets;
            lock (_lock)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                targets = _subs
                    .Where(s => s.Enabled)
                    .Where(s => listId is null || string.Equals(s.Id, listId, StringComparison.OrdinalIgnoreCase))
                    .Where(s => force || IsStale(s.Id, now))
                    .ToList();
            }

            bool changed = false;
            foreach (var sub in targets)
                changed |= await FetchOneAsync(sub).ConfigureAwait(false);
            if (changed) RebuildIndex();
        }
        catch (Exception ex)
        {
            // Fire-and-forget callers must never surface an unobserved exception
            // (e.g. the store being disposed during engine shutdown).
            Console.Error.WriteLine($"[Blocklist] refresh: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>Match one observed remote endpoint against the enabled lists. Domain entries
    /// match the resolved host name and all of its parent domains; IP entries match exactly.
    /// Returns the citing list, or null when nothing matches.</summary>
    public (string ListId, string ListName, string Matched)? Match(string remoteAddress, string remoteHost)
    {
        var index = _index;
        if (index.Ips.Count == 0 && index.Domains.Count == 0) return null;

        if (remoteAddress.Length > 0 && index.Ips.TryGetValue(remoteAddress, out string? ipList))
            return (ipList, ListName(index, ipList), remoteAddress);

        if (remoteHost.Length > 0)
        {
            string host = remoteHost.TrimEnd('.').ToLowerInvariant();
            while (host.Length > 0)
            {
                if (index.Domains.TryGetValue(host, out string? domainList))
                    return (domainList, ListName(index, domainList), host);
                int dot = host.IndexOf('.');
                if (dot < 0) break;
                host = host[(dot + 1)..];
            }
        }

        return null;
    }

    public List<BlocklistStatusItem> GetStatus()
    {
        lock (_lock)
        {
            return _subs.Select(s => new BlocklistStatusItem
            {
                Id = s.Id,
                Name = s.Name,
                Enabled = s.Enabled,
                EntryCount = _meta.TryGetValue(s.Id, out var m) ? m.EntryCount : 0,
                LastFetchUnix = _meta.TryGetValue(s.Id, out var m2) ? m2.LastFetchUnix : 0,
                LastError = _meta.TryGetValue(s.Id, out var m3) ? m3.LastError : string.Empty,
            }).ToList();
        }
    }

    private static string ListName(MatchIndex index, string listId)
        => index.ListNames.TryGetValue(listId, out string? name) ? name : listId;

    private bool IsStale(string listId, long nowUnix)
        => !_meta.TryGetValue(listId, out var meta)
            || meta.EntryCount == 0
            || nowUnix - meta.LastFetchUnix >= (long)RefreshInterval.TotalSeconds;

    private async Task<bool> FetchOneAsync(BlocklistSubscription sub)
    {
        try
        {
            string body = await _http.GetStringAsync(sub.Url).ConfigureAwait(false);
            var entries = ParseEntries(body);
            if (entries.Count == 0)
            {
                SetMeta(sub.Id, meta => meta.LastError = "No entries found in the downloaded list.");
                return false;
            }

            _store.ReplaceBlocklistEntries(sub.Id, entries);
            SetMeta(sub.Id, meta =>
            {
                meta.LastFetchUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                meta.EntryCount = entries.Count;
                meta.LastError = string.Empty;
            });
            return true;
        }
        catch (Exception ex)
        {
            SetMeta(sub.Id, meta => meta.LastError = Brief(ex));
            return false;
        }
    }

    /// <summary>
    /// Parse hosts-file ("0.0.0.0 domain" / "127.0.0.1 domain") and plain one-entry-per-line
    /// (domain or IP) formats. Comments (#, !) and hosts-file boilerplate are skipped.
    /// </summary>
    internal static List<(int Kind, string Value)> ParseEntries(string text)
    {
        var entries = new List<(int, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            if (entries.Count >= MaxEntriesPerList) break;

            var span = rawLine;
            int hash = span.IndexOf('#');
            if (hash >= 0) span = span[..hash];
            span = span.Trim();
            if (span.IsEmpty || span[0] == '!') continue;

            string line = span.ToString();
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> candidates;
            if (tokens.Length >= 2 && IPAddress.TryParse(tokens[0], out _))
            {
                // hosts-file format: only the null/loopback redirect forms are block entries.
                // A single line may list several hostnames for one redirect address.
                if (tokens[0] is not ("0.0.0.0" or "127.0.0.1" or "::" or "::1")) continue;
                candidates = tokens.Skip(1);
            }
            else if (tokens.Length == 1)
            {
                candidates = tokens;
            }
            else
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                if (entries.Count >= MaxEntriesPerList) break;

                if (IPAddress.TryParse(candidate, out var ip))
                {
                    string value = ip.ToString();
                    if (value is "0.0.0.0" or "127.0.0.1" or "::" or "::1") continue;
                    if (seen.Add("i" + value)) entries.Add((KindIp, value));
                    continue;
                }

                // Trim both trailing AND leading dots (leading-dot forms like ".ads.example"
                // appear in some domain lists but reverse-DNS names never carry one).
                string domain = candidate.Trim('.').ToLowerInvariant();
                if (domain.Length is 0 or > 253) continue;
                if (!domain.Contains('.')) continue;
                if (SkipHosts.Contains(domain)) continue;
                if (domain.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) continue;
                if (seen.Add("d" + domain)) entries.Add((KindDomain, domain));
            }
        }

        return entries;
    }

    private void RebuildIndex()
    {
        List<BlocklistSubscription> enabled;
        lock (_lock)
            enabled = _subs.Where(s => s.Enabled).ToList();

        var index = new MatchIndex();
        foreach (var sub in enabled)
        {
            index.ListNames[sub.Id] = sub.Name;
            foreach (var (kind, value) in _store.LoadBlocklistEntries(sub.Id))
            {
                if (kind == KindDomain) index.Domains.TryAdd(value, sub.Id);
                else index.Ips.TryAdd(value, sub.Id);
            }
        }

        _index = index.Domains.Count == 0 && index.Ips.Count == 0 ? MatchIndex.Empty : index;
    }

    private void SetMeta(string listId, Action<ListMeta> mutate)
    {
        lock (_lock)
        {
            if (!_meta.TryGetValue(listId, out var meta))
                _meta[listId] = meta = new ListMeta();
            mutate(meta);
            try { SaveMeta(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Blocklist] persist meta: {ex.Message}"); }
        }
    }

    private Dictionary<string, ListMeta> LoadMeta()
    {
        try
        {
            string? json = _store.GetStateValue(MetaKey);
            if (!string.IsNullOrEmpty(json))
                return JsonSerializer.Deserialize<Dictionary<string, ListMeta>>(json)
                    ?? new Dictionary<string, ListMeta>(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt state resets below */ }
        return new Dictionary<string, ListMeta>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveMeta()
        => _store.SetStateValue(MetaKey, JsonSerializer.Serialize(_meta));

    private static string Brief(Exception ex)
    {
        string message = ex.GetBaseException().Message;
        message = message.ReplaceLineEndings(" ");
        return message.Length <= 160 ? message : message[..160];
    }

    public void Dispose() => _http.Dispose();
}
