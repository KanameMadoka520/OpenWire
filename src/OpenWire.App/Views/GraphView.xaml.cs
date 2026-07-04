using System.Windows.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

public partial class GraphView : UserControl
{
    private GraphViewModel? _vm;

    public GraphView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm = DataContext as GraphViewModel;
        if (_vm is null) return;
        _vm.SeriesLoaded += OnSeriesLoaded;
        _vm.SampleReceived += OnSampleReceived;
        try { await _vm.LoadAsync(); } catch { /* engine not ready */ }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SeriesLoaded -= OnSeriesLoaded;
        _vm.SampleReceived -= OnSampleReceived;
    }

    private void OnSeriesLoaded(TrafficSeries series) => Graph.SetSeries(series);

    private void OnSampleReceived(double epochSec, double inBytes, double outBytes)
        => Graph.AddSample(epochSec, inBytes, outBytes);
}
