using System.Windows.Controls;
using System.Windows.Threading;
using OpenWire.App.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

public partial class HardwareView : UserControl
{
    private HardwareViewModel? _vm;
    // 4 Hz to match the engine's sample rate: frequent, fine graph updates read as a
    // continuous scroll rather than a once-a-second step.
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _refreshing;

    // Collapsible left process panel (GlassWire-style): hide it to give the graphs the
    // full width; the funnel button in the header brings it back. The star width is
    // remembered across collapses so a splitter-resized panel returns to its size.
    private bool _panelCollapsed;
    private System.Windows.GridLength _savedLeftWidth = new(0.44, System.Windows.GridUnitType.Star);

    public HardwareView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Skip polling while the window is hidden to the tray — IsVisible goes false then,
        // even though the timer keeps ticking.
        _timer.Tick += async (_, _) => await RefreshOnceAsync();
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm = DataContext as HardwareViewModel;
        if (_vm is null) return;
        _vm.SnapshotUpdated += OnSnapshot;
        await RefreshOnceAsync();
        _timer.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _timer.Stop();
        if (_vm is not null) _vm.SnapshotUpdated -= OnSnapshot;
    }

    private async Task RefreshOnceAsync()
    {
        if (_vm is null || !IsVisible || _refreshing) return;
        _refreshing = true;
        try { await _vm.RefreshAsync(); }
        catch { }
        finally { _refreshing = false; }
    }

    /// <summary>Switch the left panel between the process list and the hardware-resource
    /// list. Fires once during InitializeComponent (before the VM is attached) — guarded.</summary>
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null) _vm.ShowResources = ModeCombo.SelectedIndex == 1;
    }

    /// <summary>Collapse / restore the left process panel so the graphs can use the
    /// full width. Both the header funnel and the panel's × call this.</summary>
    private void OnTogglePanel(object sender, System.Windows.RoutedEventArgs e)
    {
        _panelCollapsed = !_panelCollapsed;
        if (_panelCollapsed)
        {
            _savedLeftWidth = LeftCol.Width;                 // remember a splitter-dragged size
            LeftPanel.Visibility = System.Windows.Visibility.Collapsed;
            Splitter.Visibility = System.Windows.Visibility.Collapsed;
            LeftCol.MinWidth = 0;
            LeftCol.Width = new System.Windows.GridLength(0);
        }
        else
        {
            LeftPanel.Visibility = System.Windows.Visibility.Visible;
            Splitter.Visibility = System.Windows.Visibility.Visible;
            LeftCol.MinWidth = 320;
            LeftCol.Width = _savedLeftWidth;
        }
    }

    private void OnSnapshot(HardwareSnapshot hw)
    {
        CpuGraph.SetSamples(hw.History, static s => s.CpuPercent, bytes: false);
        MemGraph.SetSamples(hw.History, static s => s.MemoryPercent, bytes: false);
        DiskGraph.SetSamples(hw.History, static s => s.DiskBytesPerSec, bytes: true);
        GpuGraph.SetSamples(hw.History, static s => s.GpuPercent, bytes: false);

        // Time axis: 7 evenly-spaced absolute times across the displayed 5-minute
        // window. The graphs scroll a fixed window ending at now - DisplayDelay,
        // regardless of how much wall time the sample buffer happens to span.
        var end = DateTimeOffset.Now - TimeSpan.FromSeconds(MetricGraph.DisplayDelay);
        var start = end - TimeSpan.FromSeconds(MetricGraph.WindowSeconds);
        var labels = new string[7];
        for (int i = 0; i < 7; i++)
            labels[i] = (start + (end - start) * (i / 6.0)).ToString("HH:mm:ss");
        TimeAxis.ItemsSource = labels;
    }
}
