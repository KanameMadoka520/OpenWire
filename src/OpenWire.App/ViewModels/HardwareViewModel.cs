using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>One process row in the Hardware screen's per-process resource table.
/// Holds the raw readings (for sorting) plus pre-formatted display strings.</summary>
public sealed class ProcResRowVM
{
    /// <summary>Raw engine row, kept so the view-model can sort on real values.</summary>
    public ProcessResourceRow Src { get; }

    public int Pid => Src.Pid;
    public string Name => Src.Name;
    public string Path => Src.ExecutablePath;

    public string CpuText { get; }
    public string GpuText { get; }
    public string MemoryText { get; }
    public string DiskText { get; }

    public ProcResRowVM(ProcessResourceRow p)
    {
        Src = p;
        CpuText = Pct(p.CpuPercent);
        GpuText = Pct(p.GpuPercent);
        MemoryText = ByteFormatter.Bytes(p.MemoryBytes);
        DiskText = p.DiskBytesPerSec >= 1 ? ByteFormatter.Rate(p.DiskBytesPerSec) : "";
    }

    // "{0:0} %" but blank when it would round to zero, so idle rows stay quiet.
    private static string Pct(double v)
    {
        var s = v.ToString("0");
        return s == "0" ? "" : $"{s} %";
    }
}

/// <summary>Hardware Resources tab — CPU / memory / disk / GPU telemetry, plus a
/// GlassWire-style per-process resource table on the left.</summary>
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

    /// <summary>Per-process rows shown in the left table. The instance is stable —
    /// contents are reconciled in place each poll so the list doesn't flash or lose
    /// its scroll position every second.</summary>
    public ObservableCollection<ProcResRowVM> Processes { get; } = new();

    // Column sort. Default: CPU descending (busiest process on top).
    [ObservableProperty] private string _sortKey = "cpu";
    [ObservableProperty] private bool _sortAscending;
    private List<ProcessResourceRow> _lastProcs = new();

    // Rebuilding the ~40-row process table swaps 40 view-models (icon lookups +
    // row re-binds) on the UI thread — the same thread that drives the graph
    // scroll. Doing that every second hitches the scroll right at each 1 s sample
    // boundary. The numbers and graphs stay at 1 Hz; the table refreshes every
    // second poll (~2 s, matching GlassWire) so the scroll runs uncontended.
    private const int ProcRebuildEveryNthTick = 2;
    private int _tick;

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

        // Graphs first (cheap, must stay smooth), then the heavier table on a
        // slower cadence. The very first poll always builds the table so it
        // isn't blank while waiting for the second tick.
        SnapshotUpdated?.Invoke(hw);

        if (_tick++ % ProcRebuildEveryNthTick == 0)
        {
            _lastProcs = hw.Processes;
            RebuildProcesses();
        }
    }

    /// <summary>Sort by a column; clicking the active column again reverses it.
    /// The chosen key survives each live poll, so rows keep a stable order instead
    /// of reshuffling as values tick.</summary>
    [RelayCommand]
    private void SortBy(string key)
    {
        if (string.Equals(key, SortKey, StringComparison.Ordinal))
            SortAscending = !SortAscending;
        else
        {
            SortKey = key;
            // Name defaults A→Z; numeric columns default high→low.
            SortAscending = key == "name";
        }
        RebuildProcesses();
    }

    /// <summary>Re-projects the latest snapshot into the sorted <see cref="Processes"/>
    /// collection, updating rows in place (by index) to avoid a full ItemsSource swap.</summary>
    private void RebuildProcesses()
    {
        var sorted = ApplySort(_lastProcs.Select(p => new ProcResRowVM(p))).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i < Processes.Count) Processes[i] = sorted[i];
            else Processes.Add(sorted[i]);
        }
        while (Processes.Count > sorted.Count) Processes.RemoveAt(Processes.Count - 1);
    }

    private IEnumerable<ProcResRowVM> ApplySort(IEnumerable<ProcResRowVM> q)
    {
        Func<ProcResRowVM, object> key = SortKey switch
        {
            "name" => r => r.Name ?? "",
            "cpu" => r => r.Src.CpuPercent,
            "gpu" => r => r.Src.GpuPercent,
            "mem" => r => (double)r.Src.MemoryBytes,
            "disk" => r => r.Src.DiskBytesPerSec,
            _ => r => r.Src.CpuPercent,
        };
        var cmp = SortKey == "name"
            ? (IComparer<object>)StringKeyComparer.Instance
            : Comparer<object>.Default;
        // Ties break deterministically by Pid so equal-value rows never swap places.
        return SortAscending
            ? q.OrderBy(key, cmp).ThenBy(r => r.Pid)
            : q.OrderByDescending(key, cmp).ThenBy(r => r.Pid);
    }

    private sealed class StringKeyComparer : IComparer<object>
    {
        public static readonly StringKeyComparer Instance = new();
        public int Compare(object? x, object? y) =>
            string.Compare(x as string, y as string, StringComparison.OrdinalIgnoreCase);
    }
}
