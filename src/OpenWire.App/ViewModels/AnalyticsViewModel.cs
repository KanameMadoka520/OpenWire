using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
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
    [ObservableProperty] private ObservableCollection<UsageAnomaly> _anomalies = new();
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
        DownUpText = $"{ByteFormatter.Bytes(r.TotalBytesIn)} down · {ByteFormatter.Bytes(r.TotalBytesOut)} up";
        BusiestHourText = r.BusiestHour < 0 ? "—" : $"{r.BusiestHour:00}:00";
        PerDayText = ByteFormatter.Bytes(r.AveragePerActiveDay);
        ActiveAppsText = r.ActiveApps.ToString();
        ActiveDaysText = $"over {r.ActiveDays} active day{(r.ActiveDays == 1 ? "" : "s")}";

        HasChange = r.PreviousTotalBytes > 0;
        ChangeText = HasChange
            ? (r.ChangeFraction >= 0 ? "▲ " : "▼ ") + Math.Abs(r.ChangeFraction).ToString("P0") + " vs previous"
            : "no prior period";

        Highlights = new ObservableCollection<string>(r.Highlights);
        Anomalies = new ObservableCollection<UsageAnomaly>(r.Anomalies);
        HasAnomalies = r.Anomalies.Count > 0;
        TopApps = new ObservableCollection<AppShare>(r.TopApps);

        ReportLoaded?.Invoke(r);
    }

    partial void OnRangeChanged(GraphRange value) => _ = LoadAsync();
}
