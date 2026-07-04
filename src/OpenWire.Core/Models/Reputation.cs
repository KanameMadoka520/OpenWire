namespace OpenWire.Core.Models;

/// <summary>Reputation verdict for an application's executable, from VirusTotal.</summary>
public enum ReputationState
{
    /// <summary>No key configured, or the file hasn't been looked at yet.</summary>
    Unknown = 0,

    /// <summary>Hashing / lookup is in flight.</summary>
    Scanning = 1,

    /// <summary>Known to VirusTotal and flagged by no engines.</summary>
    Clean = 2,

    /// <summary>Flagged by one or more engines.</summary>
    Flagged = 3,

    /// <summary>Hash not present in VirusTotal's dataset.</summary>
    NotFound = 4,

    /// <summary>Lookup failed (bad key, offline, quota, unreadable file…).</summary>
    Error = 5,
}

/// <summary>
/// A file-reputation result for an application binary, produced by the optional
/// VirusTotal integration. Rides along on <see cref="AppUsage"/> so the Firewall
/// list can show a per-app reputation column without an extra round-trip.
/// </summary>
public sealed class AppReputation
{
    public ReputationState State { get; set; } = ReputationState.Unknown;

    /// <summary>Engines that flagged the file as malicious.</summary>
    public int Malicious { get; set; }

    /// <summary>Engines that flagged the file as suspicious.</summary>
    public int Suspicious { get; set; }

    /// <summary>Total engines that analysed the file.</summary>
    public int Total { get; set; }

    /// <summary>SHA-256 of the binary (empty until computed).</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Short human-readable detail (e.g. an error reason).</summary>
    public string Detail { get; set; } = string.Empty;
}
