using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

/// <summary>Graph screen: owns the range selection, feeds the live traffic graph
/// control (via events consumed by the view), and lists the top apps.</summary>
public partial class GraphViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private GraphRange _range = GraphRange.FiveMinutes;
    [ObservableProperty] private ObservableCollection<AppUsage> _topApps = new();

    /// <summary>Raised when a full historical series has been (re)loaded.</summary>
    public event Action<TrafficSeries>? SeriesLoaded;

    /// <summary>Raised for each live per-second sample (epochSec, inBytes, outBytes).</summary>
    public event Action<double, double, double>? SampleReceived;

    public GraphViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var graph = await _client.GetGraphAsync(Range);
        SeriesLoaded?.Invoke(graph.Series);

        var usageRange = Range == GraphRange.FiveMinutes ? GraphRange.Day : Range;
        var usage = await _client.GetUsageAsync(usageRange, UsageGroupBy.Apps);
        TopApps = new ObservableCollection<AppUsage>(usage.Apps.Take(14));
    }

    public void PushTick(DateTimeOffset time, double inBytes, double outBytes)
    {
        if (Range == GraphRange.FiveMinutes)
            SampleReceived?.Invoke(time.ToUnixTimeMilliseconds() / 1000.0, inBytes, outBytes);
    }

    partial void OnRangeChanged(GraphRange value) => _ = LoadAsync();
}
