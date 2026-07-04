using OpenWire.Core.Models;

namespace OpenWire.Core.Util;

/// <summary>Durations, sampling cadence and labels for each <see cref="GraphRange"/>.</summary>
public static class GraphRangeExtensions
{
    /// <summary>Total time window covered by the range (Unlimited caps at ~5 years).</summary>
    public static TimeSpan Duration(this GraphRange range) => range switch
    {
        GraphRange.FiveMinutes => TimeSpan.FromMinutes(5),
        GraphRange.ThreeHours => TimeSpan.FromHours(3),
        GraphRange.Day => TimeSpan.FromDays(1),
        GraphRange.Week => TimeSpan.FromDays(7),
        GraphRange.Month => TimeSpan.FromDays(30),
        GraphRange.Unlimited => TimeSpan.FromDays(365 * 5),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>Preferred spacing between plotted points, to keep ~150-300 points on screen.</summary>
    public static int IntervalSeconds(this GraphRange range) => range switch
    {
        GraphRange.FiveMinutes => 1,
        GraphRange.ThreeHours => 60,
        GraphRange.Day => 60 * 5,
        GraphRange.Week => 60 * 60,
        GraphRange.Month => 60 * 60 * 4,
        GraphRange.Unlimited => 60 * 60 * 24,
        _ => 1,
    };

    public static string Label(this GraphRange range) => range switch
    {
        GraphRange.FiveMinutes => "5 minutes",
        GraphRange.ThreeHours => "3 hours",
        GraphRange.Day => "Day",
        GraphRange.Week => "Week",
        GraphRange.Month => "Month",
        GraphRange.Unlimited => "Unlimited",
        _ => range.ToString(),
    };
}
