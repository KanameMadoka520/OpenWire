namespace OpenWire.Core.Models;

/// <summary>
/// A monthly data-usage plan used to warn the user before they exceed a cap
/// (the GlassWire "Data Plan" feature).
/// </summary>
public sealed class DataPlan
{
    public bool Enabled { get; set; }

    /// <summary>Plan allowance in bytes (0 = unlimited).</summary>
    public long LimitBytes { get; set; }

    /// <summary>Day of the month the billing cycle resets (1-31).</summary>
    public int BillingCycleStartDay { get; set; } = 1;

    /// <summary>Bytes consumed so far in the current cycle.</summary>
    public long UsedBytes { get; set; }

    /// <summary>Warn when usage crosses this fraction of the limit (0..1).</summary>
    public double WarnAtFraction { get; set; } = 0.9;

    public double UsedFraction => LimitBytes > 0 ? Math.Min(1.0, (double)UsedBytes / LimitBytes) : 0;

    public long RemainingBytes => LimitBytes > 0 ? Math.Max(0, LimitBytes - UsedBytes) : 0;
}
