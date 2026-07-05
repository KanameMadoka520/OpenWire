using System;
using System.Linq;
using System.Windows.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

public partial class AnalyticsView : UserControl
{
    private AnalyticsViewModel? _vm;

    public AnalyticsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm = DataContext as AnalyticsViewModel;
        if (_vm is null) return;
        _vm.ReportLoaded += OnReport;
        try { await _vm.LoadAsync(); } catch { /* engine not ready */ }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is not null) _vm.ReportLoaded -= OnReport;
    }

    private void OnReport(InsightsReport r)
    {
        // Hour-of-day: 24 bars (00..23), busiest hour highlighted.
        var hourVals = r.HourOfDay.OrderBy(h => h.Hour).Select(h => (double)h.Total).ToList();
        var hourLabels = Enumerable.Range(0, 24).Select(h => $"{h:00}").ToList();
        HourChart.SetData(hourVals, hourLabels, r.BusiestHour, bytes: true);

        // Daily: one bar per calendar day, busiest day highlighted.
        var dayVals = r.Daily.Select(d => (double)d.Total).ToList();
        var dayLabels = r.Daily
            .Select(d => DateTimeOffset.FromUnixTimeSeconds(d.DayStartUnix).ToLocalTime().ToString("M/d"))
            .ToList();
        int peakDay = -1; double max = -1;
        for (int i = 0; i < dayVals.Count; i++)
            if (dayVals[i] > max) { max = dayVals[i]; peakDay = i; }
        DayChart.SetData(dayVals, dayLabels, peakDay, bytes: true);
    }
}
