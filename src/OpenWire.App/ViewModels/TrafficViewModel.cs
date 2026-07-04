using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

public enum TrafficSubView { Graph, Columns, Map }

/// <summary>The "Traffic Monitor" tab — folds the live graph, the usage columns and
/// a map into one screen with a sub-view toggle and a shared time range.</summary>
public partial class TrafficViewModel : ObservableObject
{
    public GraphViewModel Graph { get; }
    public UsageViewModel Usage { get; }

    [ObservableProperty] private TrafficSubView _subView = TrafficSubView.Graph;
    [ObservableProperty] private GraphRange _range = GraphRange.FiveMinutes;

    public TrafficViewModel(EngineClient client)
    {
        Graph = new GraphViewModel(client);
        Usage = new UsageViewModel(client);
    }

    public async Task LoadAsync()
    {
        await Graph.LoadAsync();
        await Usage.LoadAsync();
    }

    public void PushTick(DateTimeOffset time, double inBytes, double outBytes)
        => Graph.PushTick(time, inBytes, outBytes);

    partial void OnRangeChanged(GraphRange value)
    {
        Graph.Range = value;   // triggers Graph reload
        Usage.Range = value;   // triggers Usage reload
    }

    partial void OnSubViewChanged(TrafficSubView value)
    {
        if (value == TrafficSubView.Columns) _ = Usage.LoadAsync();
    }
}
