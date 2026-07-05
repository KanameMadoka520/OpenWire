using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.Service.Engine;

/// <summary>
/// Turns the recorded usage history into statistical anomalies — the "discover the
/// unusual" half of OpenWire's monitoring mission. Pure and stateless: it compares
/// the current day against a rolling per-app baseline and the set of countries seen
/// so far, and returns the notable deviations. The engine decides what to display
/// and what to raise as an alert.
/// </summary>
public static class AnomalyDetector
{
    // An app must move at least this much today before a spike is worth mentioning
    // (keeps chatty background apps quiet).
    private const long AppFloorBytes = 50L * 1024 * 1024;      // 50 MB

    // Today must exceed the baseline daily average by this factor to be a spike.
    private const double SpikeRatio = 4.0;

    // Upload-heavy: uploaded at least this much, and upload ≥ this × download.
    private const long UploadFloorBytes = 30L * 1024 * 1024;   // 30 MB
    private const double UploadRatio = 3.0;

    // Odd-hour: at least this much traffic in an hour that is normally idle.
    private const long OddHourFloorBytes = 20L * 1024 * 1024;  // 20 MB

    /// <summary>Per-app baseline aggregated over the trailing window (excluding today).</summary>
    public readonly record struct Baseline(string AppId, int ActiveDays, long BytesIn, long BytesOut)
    {
        public long DailyAverage => ActiveDays > 0 ? (BytesIn + BytesOut) / ActiveDays : 0;
    }

    /// <summary>A country and its friendly name whose first contact is recent.</summary>
    public readonly record struct NewCountry(string Code, string Name, long FirstSeenUnix);

    /// <summary>
    /// Compute anomalies for the current day.
    /// </summary>
    /// <param name="today">Per-app usage accumulated so far today.</param>
    /// <param name="baselines">Per-app trailing baseline (excludes today).</param>
    /// <param name="baselineDays">Number of days the baseline spans (for gating).</param>
    /// <param name="newCountries">Countries first contacted within the recent window.</param>
    /// <param name="baselineHours">Baseline traffic per local hour (0..23), excluding today.</param>
    /// <param name="todayHours">Today's traffic per local hour (0..23).</param>
    public static List<UsageAnomaly> Detect(
        IReadOnlyList<AppUsage> today,
        IReadOnlyList<Baseline> baselines,
        int baselineDays,
        IReadOnlyList<NewCountry> newCountries,
        IReadOnlyList<HourUsage> baselineHours,
        IReadOnlyList<HourUsage> todayHours)
    {
        var result = new List<UsageAnomaly>();
        var baseByApp = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in baselines) baseByApp[b.AppId] = b;

        // 1) Per-app volume spikes vs the rolling daily average. Driven purely by the
        //    per-app baseline (independent of the chart window), so it fires whenever
        //    there is at least one prior active day for the app.
        foreach (var a in today)
        {
            long total = a.Total;
            if (total < AppFloorBytes) continue;
            if (!baseByApp.TryGetValue(a.App.Id, out var b)) continue;   // no history to compare
            long avg = b.DailyAverage;
            if (avg <= 0) continue;
            double ratio = (double)total / avg;
            if (ratio < SpikeRatio) continue;

            result.Add(new UsageAnomaly
            {
                Kind = AnomalyKind.VolumeSpike,
                Severity = ratio >= SpikeRatio * 2 ? AlertSeverity.Warning : AlertSeverity.Info,
                Title = $"{a.App.Name} traffic spike",
                Detail = $"{a.App.Name} moved {ByteFormatter.Bytes(total)} today — about {ratio:0.#}× its {b.ActiveDays}-day average of {ByteFormatter.Bytes(avg)}/day.",
                AppId = a.App.Id,
                AppName = a.App.Name,
                ExecutablePath = a.App.ExecutablePath,
                ObservedBytes = total,
                BaselineBytes = avg,
                Ratio = ratio,
            });
        }

        // 2) Upload-heavy apps (bulk-upload / exfil shape).
        foreach (var a in today)
        {
            if (a.BytesOut < UploadFloorBytes) continue;
            long down = Math.Max(1, a.BytesIn);
            double ratio = (double)a.BytesOut / down;
            if (ratio < UploadRatio) continue;

            result.Add(new UsageAnomaly
            {
                Kind = AnomalyKind.UploadHeavy,
                Severity = AlertSeverity.Warning,
                Title = $"{a.App.Name} is upload-heavy",
                Detail = $"{a.App.Name} uploaded {ByteFormatter.Bytes(a.BytesOut)} today but only downloaded {ByteFormatter.Bytes(a.BytesIn)} ({ratio:0.#}× more up than down).",
                AppId = a.App.Id,
                AppName = a.App.Name,
                ExecutablePath = a.App.ExecutablePath,
                ObservedBytes = a.BytesOut,
                BaselineBytes = a.BytesIn,
                Ratio = ratio,
            });
        }

        // 3) First contact with a country not seen before.
        foreach (var c in newCountries)
        {
            result.Add(new UsageAnomaly
            {
                Kind = AnomalyKind.NewCountry,
                Severity = AlertSeverity.Info,
                Title = $"First contact with {NameOrCode(c)}",
                Detail = $"Your computer exchanged traffic with {NameOrCode(c)} for the first time in its recorded history.",
                CountryCode = c.Code,
            });
        }

        // 4) Significant traffic during a normally-idle hour. Everything is compared in
        //    per-day terms (baselineHours/todayHours totals are window sums): an hour is
        //    "normally idle" if its average day is under 5% of the busiest hour's.
        if (baselineDays >= 2 && baselineHours.Count == 24 && todayHours.Count == 24)
        {
            long peakPerDay = baselineHours.Max(h => h.Total) / baselineDays;
            if (peakPerDay > 0)
            {
                long quietThreshold = (long)(peakPerDay * 0.05);
                foreach (var th in todayHours)
                {
                    if (th.Total < OddHourFloorBytes) continue;
                    long baselinePerDay = baselineHours[th.Hour].Total / baselineDays;
                    if (baselinePerDay > quietThreshold) continue;   // this hour is normally active
                    if (th.Total < Math.Max(OddHourFloorBytes, baselinePerDay * 3)) continue;

                    result.Add(new UsageAnomaly
                    {
                        Kind = AnomalyKind.OddHour,
                        Severity = AlertSeverity.Info,
                        Title = $"Activity at an unusual hour ({th.Hour:00}:00)",
                        Detail = $"{ByteFormatter.Bytes(th.Total)} of traffic around {th.Hour:00}:00 today — a time your network is normally quiet.",
                        Hour = th.Hour,
                        ObservedBytes = th.Total,
                        BaselineBytes = baselinePerDay,
                    });
                }
            }
        }

        // Most significant first: warnings above info, then by observed volume.
        return result
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.ObservedBytes)
            .ToList();
    }

    private static string NameOrCode(NewCountry c)
        => string.IsNullOrWhiteSpace(c.Name) || c.Name == c.Code ? $"country {c.Code}" : c.Name;
}
