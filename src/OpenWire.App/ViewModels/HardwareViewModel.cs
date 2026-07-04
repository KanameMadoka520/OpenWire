using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>Hardware Resources tab — CPU / memory / disk / GPU telemetry.</summary>
public partial class HardwareViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private string _cpuText = "0 %";
    [ObservableProperty] private string _memoryText = "0 B";
    [ObservableProperty] private string _memoryPctText = "0 %";
    [ObservableProperty] private string _diskReadText = "0 B/s";
    [ObservableProperty] private string _diskWriteText = "0 B/s";
    [ObservableProperty] private string _gpuText = "0 %";
    [ObservableProperty] private string _gpuMemoryText = "0 B";

    /// <summary>Raised each poll so the view can feed the four metric graphs.</summary>
    public event Action<HardwareSnapshot>? SnapshotUpdated;

    public HardwareViewModel(EngineClient client) => _client = client;

    public Task LoadAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        HardwareSnapshot hw;
        try { hw = (await _client.GetHardwareAsync()).Hardware; }
        catch { return; }

        CpuText = $"{hw.CpuPercent:0} %";
        MemoryText = ByteFormatter.Bytes(hw.MemoryUsedBytes);
        MemoryPctText = $"{hw.MemoryPercent:0} %";
        DiskReadText = ByteFormatter.Rate(hw.DiskReadBytesPerSec);
        DiskWriteText = ByteFormatter.Rate(hw.DiskWriteBytesPerSec);
        GpuText = $"{hw.GpuPercent:0} %";
        GpuMemoryText = ByteFormatter.Bytes(hw.GpuMemoryUsedBytes);

        SnapshotUpdated?.Invoke(hw);
    }
}
