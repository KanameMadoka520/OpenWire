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
        _timer.Tick += async (_, _) => { if (_vm is not null) await _vm.RefreshAsync(); };
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm = DataContext as HardwareViewModel;
        if (_vm is null) return;
        _vm.SnapshotUpdated += OnSnapshot;
        try { await _vm.RefreshAsync(); } catch { }
        _timer.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _timer.Stop();
        if (_vm is not null) _vm.SnapshotUpdated -= OnSnapshot;
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
        // One shared timestamp array (epoch seconds) feeds all four scrolling graphs.
        int n = hw.History.Count;
        var times = new double[n];
        var cpu = new double[n];
        var mem = new double[n];
        var disk = new double[n];
        var gpu = new double[n];
        for (int i = 0; i < n; i++)
        {
            var s = hw.History[i];
            times[i] = s.Time.ToUnixTimeMilliseconds() / 1000.0;
            cpu[i] = s.CpuPercent;
            mem[i] = s.MemoryPercent;
            disk[i] = s.DiskBytesPerSec;
            gpu[i] = s.GpuPercent;
        }
        CpuGraph.SetSamples(times, cpu, bytes: false);
        MemGraph.SetSamples(times, mem, bytes: false);
        DiskGraph.SetSamples(times, disk, bytes: true);
        GpuGraph.SetSamples(times, gpu, bytes: false);

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
