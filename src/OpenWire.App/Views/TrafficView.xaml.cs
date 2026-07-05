using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

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
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not TrafficViewModel vm) return;
        _vm = vm;
        _graph = vm.Graph;
        _graph.SeriesLoaded += OnSeriesLoaded;
        _graph.SampleReceived += OnSampleReceived;
        _graph.PinRequested += OnPinRequested;
        vm.Usage.PropertyChanged += OnUsageChanged;
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
    }

    private void OnSeriesLoaded(TrafficSeries series) => Graph.SetSeries(series);
    private void OnSampleReceived(double epochSec, double inBytes, double outBytes) => Graph.AddSample(epochSec, inBytes, outBytes);
    private void OnPinRequested(double epochSec, string label) => Graph.AddPin(epochSec, label);

    private void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageViewModel.Countries)) PushMap();
    }

    private void PushMap() => Map.SetData(_vm?.Usage.Countries.ToList() ?? new System.Collections.Generic.List<CountryUsage>());

    private void OnTogglePause(object sender, System.Windows.RoutedEventArgs e)
    {
        Graph.Paused = !Graph.Paused;
        PauseBtn.Content = Graph.Paused ? Glyph(0xE768) : Glyph(0xE769); // play / pause
        PauseBtn.ToolTip = Graph.Paused ? "Resume the live graph" : "Pause the live graph";
    }
}
