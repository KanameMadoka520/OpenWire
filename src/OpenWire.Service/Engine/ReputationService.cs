using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Optional VirusTotal file-reputation lookups for application binaries. Uses the
/// user's own free API key (never bundled) supplied via settings; when no key is
/// set the service is inert and every lookup returns null.
///
/// Work is done on a single background worker: paths are hashed (SHA-256) and the
/// digest is looked up through VirusTotal's v3 <c>/files/{sha256}</c> endpoint.
/// Requests are paced to stay inside the public free tier (4 lookups/min) and
/// results are cached per path so a binary is only hashed and queried once.
/// </summary>
public sealed class ReputationService : IDisposable
{
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(16); // ~4/min free tier

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, AppReputation> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    private volatile string _apiKey = string.Empty;

    public ReputationService()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://www.virustotal.com/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", "OpenWire/0.1");
        _ = Task.Run(() => WorkerAsync(_cts.Token));
    }

    public bool Enabled => _apiKey.Length > 0;

    /// <summary>Update the API key (empty disables the integration).</summary>
    public void SetApiKey(string? key) => _apiKey = (key ?? string.Empty).Trim();

    /// <summary>
    /// Return the cached reputation for a binary, kicking off a background lookup
    /// the first time it's seen. Returns null when the integration is disabled.
    /// </summary>
    public AppReputation? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_byPath.TryGetValue(path, out var cached)) return cached;
        if (!Enabled) return null;

        var scanning = new AppReputation { State = ReputationState.Scanning };
        if (_byPath.TryAdd(path, scanning)) Enqueue(path);
        return scanning;
    }

    private void Enqueue(string path)
    {
        lock (_lock) { if (!_inflight.Add(path)) return; }
        if (!_queue.Writer.TryWrite(path))
            lock (_lock) _inflight.Remove(path);
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ProcessAsync(path, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _byPath[path] = new AppReputation { State = ReputationState.Error, Detail = ex.Message }; }
                finally { lock (_lock) _inflight.Remove(path); }

                try { await Task.Delay(RequestSpacing, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        if (!Enabled) { _byPath[path] = new AppReputation { State = ReputationState.Unknown }; return; }

        string sha;
        try { sha = await ComputeSha256Async(path, ct).ConfigureAwait(false); }
        catch (Exception ex) { _byPath[path] = new AppReputation { State = ReputationState.Error, Detail = "hash: " + ex.Message }; return; }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v3/files/{sha}");
        req.Headers.Add("x-apikey", _apiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        AppReputation rep = resp.StatusCode switch
        {
            HttpStatusCode.NotFound => new AppReputation { State = ReputationState.NotFound, Sha256 = sha },
            HttpStatusCode.Unauthorized => new AppReputation { State = ReputationState.Error, Sha256 = sha, Detail = "invalid API key" },
            HttpStatusCode.TooManyRequests => new AppReputation { State = ReputationState.Error, Sha256 = sha, Detail = "quota exceeded" },
            _ when resp.IsSuccessStatusCode => Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), sha),
            _ => new AppReputation { State = ReputationState.Error, Sha256 = sha, Detail = $"HTTP {(int)resp.StatusCode}" },
        };
        _byPath[path] = rep;
    }

    private static AppReputation Parse(string json, string sha)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var stats = doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("last_analysis_stats");
            int Read(string k) => stats.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            int mal = Read("malicious"), sus = Read("suspicious");
            int total = mal + sus + Read("harmless") + Read("undetected") + Read("timeout");
            return new AppReputation
            {
                Sha256 = sha,
                Malicious = mal,
                Suspicious = sus,
                Total = total,
                State = mal + sus > 0 ? ReputationState.Flagged : ReputationState.Clean,
            };
        }
        catch (Exception ex)
        {
            return new AppReputation { Sha256 = sha, State = ReputationState.Error, Detail = "parse: " + ex.Message };
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _http.Dispose();
        _cts.Dispose();
    }
}
