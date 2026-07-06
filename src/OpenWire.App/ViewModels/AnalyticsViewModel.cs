using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>
/// Analytics tab: turns the recorded history into readable statistics — when the
/// network is used (hour-of-day, per-day), the top applications, auto-generated
/// highlights, and the anomalies OpenWire detected — over a chosen window.
/// </summary>
public partial class AnalyticsViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private GraphRange _range = GraphRange.Week;

    // Custom [from, to] window (manual date + time). When active, the presets are ignored and
    // the report covers exactly the chosen span.
    [ObservableProperty] private bool _isCustomRange;
    [ObservableProperty] private string _customRangeText = "";

    public int[] Years { get; }
    public int[] Months { get; } = Enumerable.Range(1, 12).ToArray();
    public int[] Days { get; } = Enumerable.Range(1, 31).ToArray();
    public int[] Hours { get; } = Enumerable.Range(0, 24).ToArray();
    public int[] Minutes { get; } = Enumerable.Range(0, 60).ToArray();

    [ObservableProperty] private int _fromY, _fromMo = 1, _fromD = 1, _fromH, _fromMin;
    [ObservableProperty] private int _toY, _toMo = 1, _toD = 1, _toH = 23, _toMin = 59;
    private long _customFromUnix, _customToUnix;

    // Summary cards
    [ObservableProperty] private string _totalText = "0 B";
    [ObservableProperty] private string _downUpText = "";
    [ObservableProperty] private string _changeText = "";
    [ObservableProperty] private bool _hasChange;
    [ObservableProperty] private string _busiestHourText = "—";
    [ObservableProperty] private string _perDayText = "0 B";
    [ObservableProperty] private string _activeAppsText = "0";
    [ObservableProperty] private string _activeDaysText = "";

    [ObservableProperty] private ObservableCollection<string> _highlights = new();
    [ObservableProperty] private ObservableCollection<AnomalyRow> _anomalies = new();
    [ObservableProperty] private ObservableCollection<AppShare> _topApps = new();
    [ObservableProperty] private bool _hasAnomalies;

    // Long lists show only the first few rows inline; the rest live behind a "+N more…"
    // button that opens a floating popup with the full list, so the page stays compact.
    private const int PreviewCount = 5;
    [ObservableProperty] private ObservableCollection<AnomalyRow> _anomaliesPreview = new();
    [ObservableProperty] private ObservableCollection<AppShare> _topAppsPreview = new();
    [ObservableProperty] private bool _hasMoreAnomalies;
    [ObservableProperty] private bool _hasMoreTopApps;
    [ObservableProperty] private string _moreAnomaliesText = "";
    [ObservableProperty] private string _moreTopAppsText = "";
    [ObservableProperty] private string _allAnomaliesText = "";
    [ObservableProperty] private string _allTopAppsText = "";

    /// <summary>Raised after a load so the view can feed the two bar charts.</summary>
    public event Action<InsightsReport>? ReportLoaded;

    public AnalyticsViewModel(EngineClient client)
    {
        _client = client;
        var now = DateTime.Now;
        Years = Enumerable.Range(now.Year - 2, 3).ToArray(); // last two years + current
        var start = now.Date;                                // default span: today 00:00 → now
        FromY = start.Year; FromMo = start.Month; FromD = start.Day; FromH = 0; FromMin = 0;
        ToY = now.Year; ToMo = now.Month; ToD = now.Day; ToH = now.Hour; ToMin = now.Minute;
    }

    public async Task LoadAsync()
    {
        InsightsReport r;
        try
        {
            var resp = IsCustomRange
                ? await _client.GetInsightsAsync(_customFromUnix, _customToUnix)
                : await _client.GetInsightsAsync(Range);
            r = resp.Report;
        }
        catch { return; }

        TotalText = ByteFormatter.Bytes(r.TotalBytes);
        DownUpText = string.Format(Loc.S("L.Analytics.DownUpFmt"),
            ByteFormatter.Bytes(r.TotalBytesIn), ByteFormatter.Bytes(r.TotalBytesOut));
        BusiestHourText = r.BusiestHour < 0 ? Loc.S("L.Analytics.BusiestNone") : $"{r.BusiestHour:00}:00";
        PerDayText = ByteFormatter.Bytes(r.AveragePerActiveDay);
        ActiveAppsText = r.ActiveApps.ToString();
        ActiveDaysText = string.Format(
            Loc.S(r.ActiveDays == 1 ? "L.Analytics.ActiveDaysOneFmt" : "L.Analytics.ActiveDaysManyFmt"),
            r.ActiveDays);

        HasChange = r.PreviousTotalBytes > 0;
        ChangeText = HasChange
            ? string.Format(Loc.S("L.Analytics.ChangeFmt"),
                r.ChangeFraction >= 0 ? "▲ " : "▼ ", Math.Abs(r.ChangeFraction).ToString("P0"))
            : Loc.S("L.Analytics.NoPriorPeriod");

        Highlights = new ObservableCollection<string>(BuildHighlights(r));

        var anomalies = r.Anomalies.ConvertAll(a => new AnomalyRow(a));
        Anomalies = new ObservableCollection<AnomalyRow>(anomalies);
        AnomaliesPreview = new ObservableCollection<AnomalyRow>(anomalies.Take(PreviewCount));
        HasAnomalies = anomalies.Count > 0;
        HasMoreAnomalies = anomalies.Count > PreviewCount;
        MoreAnomaliesText = HasMoreAnomalies ? string.Format(Loc.S("L.Analytics.ShowMoreFmt"), anomalies.Count - PreviewCount) : "";
        AllAnomaliesText = string.Format(Loc.S("L.Analytics.AllAnomaliesFmt"), anomalies.Count);

        TopApps = new ObservableCollection<AppShare>(r.TopApps);
        TopAppsPreview = new ObservableCollection<AppShare>(r.TopApps.Take(PreviewCount));
        HasMoreTopApps = r.TopApps.Count > PreviewCount;
        MoreTopAppsText = HasMoreTopApps ? string.Format(Loc.S("L.Analytics.ShowMoreFmt"), r.TopApps.Count - PreviewCount) : "";
        AllTopAppsText = string.Format(Loc.S("L.Analytics.AllAppsFmt"), r.TopApps.Count);

        ReportLoaded?.Invoke(r);
    }

    /// <summary>
    /// Rebuilds the localized highlight one-liners app-side from the report's
    /// structured fields, mirroring the engine's guards (MonitorEngine.BuildHighlights)
    /// so the list is fully translated without any engine changes.
    /// </summary>
    private static List<string> BuildHighlights(InsightsReport r)
    {
        var h = new List<string>();

        if (r.TotalBytes == 0)
        {
            h.Add(Loc.S("L.Analytics.HlNoActivity"));
            return h;
        }

        if (r.BusiestHour >= 0)
            h.Add(string.Format(Loc.S("L.Analytics.HlBusiest"), $"{r.BusiestHour:00}"));

        if (r.TopApps.Count > 0 && r.TopApps[0].Fraction > 0)
            h.Add(string.Format(Loc.S("L.Analytics.HlTopApp"),
                r.TopApps[0].Name, r.TopApps[0].Fraction.ToString("P0"),
                ByteFormatter.Bytes(r.TopApps[0].Total)));

        if (r.ActiveDays > 0)
            h.Add(string.Format(Loc.S("L.Analytics.HlAvgPerDay"),
                ByteFormatter.Bytes(r.AveragePerActiveDay), r.ActiveDays));

        if (r.TotalBytesIn > 0 && r.TotalBytesOut > 0)
            h.Add(string.Format(Loc.S("L.Analytics.HlSplit"),
                ByteFormatter.Bytes(r.TotalBytesIn), ByteFormatter.Bytes(r.TotalBytesOut)));

        if (r.Anomalies.Count > 0)
            h.Add(string.Format(
                Loc.S(r.Anomalies.Count == 1 ? "L.Analytics.HlAnomaliesOne" : "L.Analytics.HlAnomaliesMany"),
                r.Anomalies.Count));

        return h;
    }

    partial void OnRangeChanged(GraphRange value)
    {
        IsCustomRange = false; // a preset overrides any active custom window
        _ = LoadAsync();
    }

    /// <summary>Apply the manually-picked [from, to] window and reload. Tolerates a reversed
    /// pair and clamps the day to the month; a zero-length span is ignored.</summary>
    public async Task ApplyCustomRangeAsync()
    {
        var from = SafeDate(FromY, FromMo, FromD, FromH, FromMin);
        var to = SafeDate(ToY, ToMo, ToD, ToH, ToMin);
        if (to < from) (from, to) = (to, from);
        if (to <= from) return;
        _customFromUnix = new DateTimeOffset(from).ToUnixTimeSeconds();
        _customToUnix = new DateTimeOffset(to).ToUnixTimeSeconds();
        var c = System.Globalization.CultureInfo.GetCultureInfo(LangManager.CultureName(LangManager.Current));
        CustomRangeText = $"{from.ToString("MMM d  HH:mm", c)} – {to.ToString("MMM d  HH:mm", c)}";
        IsCustomRange = true;
        await LoadAsync();
    }

    private static DateTime SafeDate(int y, int mo, int d, int h, int mi)
    {
        y = Math.Clamp(y, 2000, 2100);
        mo = Math.Clamp(mo, 1, 12);
        d = Math.Clamp(d, 1, DateTime.DaysInMonth(y, mo));
        return new DateTime(y, mo, d, Math.Clamp(h, 0, 23), Math.Clamp(mi, 0, 59), 0, DateTimeKind.Local);
    }
}
