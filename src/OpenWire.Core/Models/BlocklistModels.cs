namespace OpenWire.Core.Models;

/// <summary>
/// A subscription to a community host blocklist (hosts-file or plain domain/IP format).
/// The engine downloads enabled lists, caches the entries locally, and matches observed
/// connections against them to raise <see cref="AlertKind.SuspiciousHost"/> alerts.
/// All lists ship disabled — downloading is an opt-in network call.
/// </summary>
public sealed class BlocklistSubscription
{
    /// <summary>Stable identifier. Built-in presets use fixed ids ("urlhaus", "stevenblack",
    /// "peterlowe"); user-added lists get a generated id. Never displayed.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name shown in the Firewall screen's blocklist panel.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>HTTP(S) source of the list. Fetched at most every few hours while enabled.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether this list is downloaded and matched. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>True for the built-in presets (cannot be removed, only disabled).</summary>
    public bool IsPreset { get; set; }
}

/// <summary>Per-list runtime status reported by the engine (cache freshness, size, errors).</summary>
public sealed class BlocklistStatusItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>Number of cached entries (domains + IPs) for this list.</summary>
    public int EntryCount { get; set; }

    /// <summary>Unix seconds of the last successful download (0 = never fetched).</summary>
    public long LastFetchUnix { get; set; }

    /// <summary>Short human-readable error from the last fetch attempt, empty when it succeeded.</summary>
    public string LastError { get; set; } = string.Empty;
}
