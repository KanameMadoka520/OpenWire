using System.Collections.ObjectModel;
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

    /// <summary>Raised after a load so the view can feed the two bar charts.</summary>
    public event Action<InsightsReport>? ReportLoaded;

    public AnalyticsViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        InsightsReport r;
        try { r = (await _client.GetInsightsAsync(Range)).Report; }
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
        Anomalies = new ObservableCollection<AnomalyRow>(r.Anomalies.ConvertAll(a => new AnomalyRow(a)));
        HasAnomalies = r.Anomalies.Count > 0;
        TopApps = new ObservableCollection<AppShare>(r.TopApps);

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

    partial void OnRangeChanged(GraphRange value) => _ = LoadAsync();
}
