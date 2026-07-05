using System.Windows.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

public partial class TrafficView : UserControl
{
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
        _graph = vm.Graph;
        _graph.SeriesLoaded += OnSeriesLoaded;
        _graph.SampleReceived += OnSampleReceived;
        _graph.PinRequested += OnPinRequested;
        try { await vm.LoadAsync(); } catch { /* engine not ready */ }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_graph is null) return;
        _graph.SeriesLoaded -= OnSeriesLoaded;
        _graph.SampleReceived -= OnSampleReceived;
        _graph.PinRequested -= OnPinRequested;
    }

    private void OnSeriesLoaded(TrafficSeries series) => Graph.SetSeries(series);
    private void OnSampleReceived(double epochSec, double inBytes, double outBytes) => Graph.AddSample(epochSec, inBytes, outBytes);
    private void OnPinRequested(double epochSec, string label) => Graph.AddPin(epochSec, label);
}
