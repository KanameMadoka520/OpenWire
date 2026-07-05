namespace OpenWire.Core.Models;

/// <summary>
/// A computed analytics report over a time window: usage totals, when-you-use-the-
/// network patterns (hour-of-day, per-day trend), the top talkers, detected usage
/// anomalies and a handful of plain-language highlights. Built entirely from the
/// recorded rollups — no live capture required, so it works non-elevated too.
/// </summary>
public sealed class InsightsReport
{
    public GraphRange Range { get; set; }

    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public long TotalBytes => TotalBytesIn + TotalBytesOut;

    /// <summary>Total bytes over the immediately-preceding window of equal length.</summary>
    public long PreviousTotalBytes { get; set; }

    /// <summary>(current - previous) / previous; 0 when there is no prior data.</summary>
    public double ChangeFraction { get; set; }

    /// <summary>Distinct local calendar days with any recorded traffic in the window.</summary>
    public int ActiveDays { get; set; }

    /// <summary>Distinct applications that moved bytes in the window.</summary>
    public int ActiveApps { get; set; }

    /// <summary>Local hour (0..23) with the most traffic across the window; -1 if none.</summary>
    public int BusiestHour { get; set; } = -1;

    /// <summary>Average bytes moved per active day.</summary>
    public long AveragePerActiveDay { get; set; }

    /// <summary>24 entries, one per local hour-of-day, summed across the window.</summary>
    public List<HourUsage> HourOfDay { get; set; } = new();

    /// <summary>One entry per local calendar day in the window (oldest → newest).</summary>
    public List<DayUsage> Daily { get; set; } = new();

    /// <summary>Top applications by total bytes, with their share of the window.</summary>
    public List<AppShare> TopApps { get; set; } = new();

    /// <summary>Detected usage anomalies (spikes, upload-heavy apps, new countries, odd hours).</summary>
    public List<UsageAnomaly> Anomalies { get; set; } = new();

    /// <summary>Human-readable one-liners summarising the notable regularities.</summary>
    public List<string> Highlights { get; set; } = new();
}

/// <summary>Traffic attributed to one local hour-of-day (0..23), summed over the window.</summary>
public sealed class HourUsage
{
    public int Hour { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;
}

/// <summary>Traffic for a single local calendar day.</summary>
public sealed class DayUsage
{
    /// <summary>Unix seconds at local midnight of the day.</summary>
    public long DayStartUnix { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;
}

/// <summary>One application's share of total traffic in the window.</summary>
public sealed class AppShare
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;
    public double Fraction { get; set; }
}

/// <summary>The nature of a detected usage anomaly.</summary>
public enum AnomalyKind
{
    /// <summary>An app moved far more than its recent daily baseline.</summary>
    VolumeSpike = 0,

    /// <summary>An app's upload dwarfs its download (possible bulk upload / exfil shape).</summary>
    UploadHeavy = 1,

    /// <summary>Traffic reached a country not seen in the baseline window.</summary>
    NewCountry = 2,

    /// <summary>Significant traffic during hours that are normally idle.</summary>
    OddHour = 3,
}

/// <summary>A single anomaly surfaced on the Analytics screen (and optionally alerted).</summary>
public sealed class UsageAnomaly
{
    public AnomalyKind Kind { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    // Optional associations for iconography / drill-down.
    public string AppId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>What we observed, and the baseline it is being compared against.</summary>
    public long ObservedBytes { get; set; }
    public long BaselineBytes { get; set; }

    /// <summary>How many times the baseline the observation is (0 when not applicable).</summary>
    public double Ratio { get; set; }

    /// <summary>Stable key for de-duplicating repeat alerts within a day.</summary>
    public string DedupeKey => $"{Kind}|{AppId}|{CountryCode}";
}
