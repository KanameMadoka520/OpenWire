using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using OpenWire.App.Controls;
using OpenWire.App.Util;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Views;

public partial class TrafficView : UserControl
{
    // Segoe Fluent glyphs (built by code point to avoid embedding private-use chars).
    private static string Glyph(int code) => ((char)code).ToString();

    private TrafficViewModel? _vm;
    private GraphViewModel? _graph;

    public TrafficView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Control events (not VM events): safe to wire once for the view's lifetime.
        Graph.RangeSelected += OnGraphRangeSelected;
        Graph.SelectionCleared += OnGraphSelectionCleared;
        Filter.CloseRequested += () => Filter.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void OnToggleFilter(object sender, System.Windows.RoutedEventArgs e)
        => Filter.Visibility = Filter.Visibility == System.Windows.Visibility.Visible
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    private void OnGraphRangeSelected(double fromSec, double toSec, double downBytes, double upBytes)
        => _vm?.ApplySelection(fromSec, toSec, downBytes, upBytes);

    private void OnGraphSelectionCleared() => _vm?.ClearSelection();

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not TrafficViewModel vm) return;
        _vm = vm;
        _graph = vm.Graph;
        _graph.SeriesLoaded += OnSeriesLoaded;
        _graph.SampleReceived += OnSampleReceived;
        _graph.PinRequested += OnPinRequested;
        vm.Usage.PropertyChanged += OnUsageChanged;
        Map.CountrySelected += OnCountrySelected;
        Strip.SelectionPreview += OnStripPreview;
        Strip.SelectionChanged += OnStripSelection;
        try { await vm.LoadAsync(); } catch { /* engine not ready */ }
        PushMap();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_graph is not null)
        {
            _graph.SeriesLoaded -= OnSeriesLoaded;
            _graph.SampleReceived -= OnSampleReceived;
            _graph.PinRequested -= OnPinRequested;
        }
        if (_vm is not null) _vm.Usage.PropertyChanged -= OnUsageChanged;
        Map.CountrySelected -= OnCountrySelected;
        Strip.SelectionPreview -= OnStripPreview;
        Strip.SelectionChanged -= OnStripSelection;
    }

    private void OnSeriesLoaded(TrafficSeries series)
    {
        Graph.SetSeries(series);
        Strip.SetSeries(series);
    }

    private void OnSampleReceived(double epochSec, double inBytes, double outBytes)
    {
        Graph.AddSample(epochSec, inBytes, outBytes);
        Strip.AddSample(epochSec, inBytes, outBytes);
    }

    private void OnPinRequested(double epochSec, string label) => Graph.AddPin(epochSec, label);

    private void OnStripPreview(double fromSec, double toSec) => Graph.PreviewWindow(fromSec, toSec);

    private void OnStripSelection(double fromSec, double toSec, bool isFull, bool pinnedRight)
    {
        if (isFull) Graph.ResetZoom();
        else if (pinnedRight && Graph.IsLiveRange) Graph.SetLiveWindow(toSec - fromSec);
        else Graph.ZoomTo(fromSec, toSec);
    }

    private void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageViewModel.Countries) || e.PropertyName == nameof(UsageViewModel.Hosts))
        {
            PushMap();
            if (DrillPanel.Visibility == System.Windows.Visibility.Visible && _drillIso is not null)
                OnCountrySelected(_drillIso); // refresh the open drill-down after a reload
        }
    }

    private void PushMap() => Map.SetData(_vm?.Usage.Countries.ToList() ?? new System.Collections.Generic.List<CountryUsage>());

    // ---- map projection toggle ----
    private void OnFlatMode(object sender, System.Windows.RoutedEventArgs e) { if (Map is not null) Map.Mode = WorldMap.MapMode.Flat; }
    private void OnGlobeMode(object sender, System.Windows.RoutedEventArgs e) { if (Map is not null) Map.Mode = WorldMap.MapMode.Globe; }
    private void OnResetView(object sender, System.Windows.RoutedEventArgs e) => Map.ResetView();

    // ---- per-country drill-down ----
    private string? _drillIso;

    private void OnCountrySelected(string iso)
    {
        if (string.IsNullOrEmpty(iso) || _vm is null) return;
        _drillIso = iso;

        var hosts = _vm.Usage.Hosts
            .Where(h => string.Equals(h.Geo.CountryCode, iso, System.StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Total)
            .ToList();
        var country = _vm.Usage.Countries.FirstOrDefault(c => string.Equals(c.CountryCode, iso, System.StringComparison.OrdinalIgnoreCase));

        DrillCode.Text = OneChina.DisplayCode(iso);
        DrillName.Text = OneChina.DisplayName(iso, country?.CountryName ?? "");
        DrillFlag.Source = new CountryFlagConverter().Convert(iso, typeof(ImageSource), null, CultureInfo.InvariantCulture) as ImageSource;
        DrillHosts.ItemsSource = hosts;
        DrillEmpty.Visibility = hosts.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        long total = hosts.Count > 0 ? hosts.Sum(h => h.Total) : (country?.Total ?? 0);
        DrillFooter.Text = string.Format(Loc.S(hosts.Count == 1 ? "L.Traffic.DrillFooterOne" : "L.Traffic.DrillFooterMany"),
            hosts.Count, ByteFormatter.Bytes(total));
        DrillPanel.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnDrillClose(object sender, System.Windows.RoutedEventArgs e)
    {
        DrillPanel.Visibility = System.Windows.Visibility.Collapsed;
        _drillIso = null;
    }

    private void OnTogglePause(object sender, System.Windows.RoutedEventArgs e)
    {
        Graph.Paused = !Graph.Paused;
        PauseBtn.Content = Graph.Paused ? Glyph(0xE768) : Glyph(0xE769); // play / pause
        PauseBtn.ToolTip = Graph.Paused ? Loc.S("L.Traffic.ResumeLiveGraph") : Loc.S("L.Traffic.PauseLiveGraph");
    }
}
