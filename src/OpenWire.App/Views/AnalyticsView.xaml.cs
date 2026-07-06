using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using OpenWire.App.Controls;
using OpenWire.App.Services;
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

    // Custom range: selecting the Custom segment loads a default custom window (today 00:00 → now)
    // and opens the picker to refine; clicking it again just reopens the picker. Apply reloads,
    // Cancel just closes (the default stays).
    private async void OnCustomChecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is null) return;
        try { await _vm.ApplyCustomRangeAsync(); } catch { }
        RangePicker.IsOpen = true;
    }

    private void OnCustomClicked(object sender, System.Windows.RoutedEventArgs e) => RangePicker.IsOpen = true;

    private async void OnApplyCustom(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is not null) { try { await _vm.ApplyCustomRangeAsync(); } catch { } }
        RangePicker.IsOpen = false;
    }

    private void OnCancelCustom(object sender, System.Windows.RoutedEventArgs e) => RangePicker.IsOpen = false;

    // "+N more…" overflow popups (StaysOpen=False closes them on an outside click).
    private void OnShowAllApps(object sender, System.Windows.RoutedEventArgs e) => TopAppsPopup.IsOpen = true;
    private void OnCloseAppsPopup(object sender, System.Windows.RoutedEventArgs e) => TopAppsPopup.IsOpen = false;
    private void OnShowAllAnomalies(object sender, System.Windows.RoutedEventArgs e) => AnomaliesPopup.IsOpen = true;
    private void OnCloseAnomaliesPopup(object sender, System.Windows.RoutedEventArgs e) => AnomaliesPopup.IsOpen = false;

    private void OnReport(InsightsReport r)
    {
        // Hour-of-day: 24 bars (00..23), busiest hour highlighted.
        var hours = r.HourOfDay.OrderBy(h => h.Hour).ToList();
        var hourVals = hours.Select(h => (double)h.Total).ToList();
        var hourLabels = hours.Select(h => $"{h.Hour:00}").ToList();
        var hourDetails = hours.Select(h => Detail($"{h.Hour:00}:00", h.BytesIn, h.BytesOut, h.TopApps)).ToList();
        HourChart.SetData(hourVals, hourLabels, r.BusiestHour, bytes: true, hourDetails);

        // Daily: one bar per calendar day, busiest day highlighted.
        var dayVals = r.Daily.Select(d => (double)d.Total).ToList();
        var dayLabels = r.Daily
            .Select(d => DateTimeOffset.FromUnixTimeSeconds(d.DayStartUnix).ToLocalTime().ToString("M/d"))
            .ToList();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(LangManager.CultureName(LangManager.Current));
        var dayDetails = r.Daily
            .Select(d => Detail(DateTimeOffset.FromUnixTimeSeconds(d.DayStartUnix).ToLocalTime().ToString("ddd, MMM d", culture),
                d.BytesIn, d.BytesOut, d.TopApps))
            .ToList();
        int peakDay = -1; double max = -1;
        for (int i = 0; i < dayVals.Count; i++)
            if (dayVals[i] > max) { max = dayVals[i]; peakDay = i; }
        DayChart.SetData(dayVals, dayLabels, peakDay, bytes: true, dayDetails);
    }

    /// <summary>Packs a bar's figures into the tooltip detail the BarChart renders on hover.</summary>
    private static BarChart.BarDetail Detail(string header, long bytesIn, long bytesOut, List<AppShare> apps)
        => new()
        {
            Header = header,
            BytesIn = bytesIn,
            BytesOut = bytesOut,
            TopApps = apps.Select(a => new BarChart.BarApp(a.Name, a.ExecutablePath, a.Total)).ToList(),
        };
}
