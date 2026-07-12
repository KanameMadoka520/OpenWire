namespace OpenWire.Core.Models;

/// <summary>
/// A per-application data cap. The engine counts an app's bytes over the current period
/// and raises a <see cref="AlertKind.DataQuotaReached"/> alert as it approaches and reaches
/// the limit. Enforcement (a firewall block when exceeded) is opt-in per quota and off by
/// default, so a quota only informs unless the user explicitly asks it to block.
/// </summary>
public sealed class AppQuota
{
    /// <summary>Application id (the lower-cased executable path), matching the firewall/usage key.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Full executable path, needed to program a firewall rule when auto-block is on.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI and alerts.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Cap in bytes for the period. Must be positive to be meaningful.</summary>
    public long LimitBytes { get; set; }

    /// <summary>How often the counter resets.</summary>
    public QuotaPeriod Period { get; set; } = QuotaPeriod.Monthly;

    /// <summary>
    /// When the app exceeds the cap, add an OpenWire firewall block rule (both directions)
    /// so it is cut off until the period resets. Off by default — a quota informs, it does not
    /// block, unless the user switches this on.
    /// </summary>
    public bool AutoBlock { get; set; } = false;
}
