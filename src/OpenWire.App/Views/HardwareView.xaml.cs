using System.Windows.Controls;
using System.Windows.Threading;
using OpenWire.App.Controls;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

public partial class HardwareView : UserControl
{
    private HardwareViewModel? _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

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
