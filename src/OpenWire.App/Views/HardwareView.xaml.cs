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
        var cpu = hw.History.Select(h => h.CpuPercent).ToList();
        var mem = hw.History.Select(h => h.MemoryPercent).ToList();
        var disk = hw.History.Select(h => h.DiskBytesPerSec).ToList();
        var gpu = hw.History.Select(h => h.GpuPercent).ToList();

        CpuGraph.SetValues(cpu, 100, bytes: false);
        MemGraph.SetValues(mem, 100, bytes: false);
        DiskGraph.SetValues(disk, Math.Max(disk.DefaultIfEmpty(0).Max(), 1_000_000), bytes: true);
        GpuGraph.SetValues(gpu, Math.Max(gpu.DefaultIfEmpty(0).Max(), 100), bytes: false);
    }
}
