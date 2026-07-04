using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
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
        CpuGraph.SetValues(hw.History.Select(h => h.CpuPercent).ToList(), bytes: false);
        MemGraph.SetValues(hw.History.Select(h => h.MemoryPercent).ToList(), bytes: false);
        DiskGraph.SetValues(hw.History.Select(h => h.DiskBytesPerSec).ToList(), bytes: true);
        GpuGraph.SetValues(hw.History.Select(h => h.GpuPercent).ToList(), bytes: false);

        // time axis: 7 evenly-spaced absolute times spanning the history window
        if (hw.History.Count >= 2)
        {
            var start = hw.History[0].Time.ToLocalTime();
            var end = hw.History[^1].Time.ToLocalTime();
            var labels = new string[7];
            for (int i = 0; i < 7; i++)
            {
                var t = start + (end - start) * (i / 6.0);
                labels[i] = t.ToString("HH:mm:ss");
            }
            TimeAxis.ItemsSource = labels;
        }
    }
}
