using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

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

    // Graph bottom breakdown bar (down/up for the view + top app + top host).
    [ObservableProperty] private string _breakdownDown = "0 B";
    [ObservableProperty] private string _breakdownUp = "0 B";
    [ObservableProperty] private string _topApp = "";
    [ObservableProperty] private string _topHost = "";

    public TrafficViewModel(EngineClient client)
    {
        Graph = new GraphViewModel(client);
        Usage = new UsageViewModel(client);
    }

    public async Task LoadAsync()
    {
        await Graph.LoadAsync();
        await Usage.LoadAsync();
        UpdateBreakdown();
    }

    private void UpdateBreakdown()
    {
        var apps = Usage.Apps;
        var hosts = Usage.Hosts;
        BreakdownDown = ByteFormatter.Bytes(apps.Sum(a => a.BytesIn));
        BreakdownUp = ByteFormatter.Bytes(apps.Sum(a => a.BytesOut));
        TopApp = apps.Count > 0 ? apps[0].App.Name + (apps.Count > 1 ? $"  +{apps.Count - 1} more" : "") : "";
        TopHost = hosts.Count > 0 ? hosts[0].Host + (hosts.Count > 1 ? $"  +{hosts.Count - 1} more" : "") : "";
    }

    public void PushTick(DateTimeOffset time, double inBytes, double outBytes)
        => Graph.PushTick(time, inBytes, outBytes);

    partial void OnRangeChanged(GraphRange value)
    {
        Graph.Range = value;   // triggers Graph reload
        Usage.Range = value;   // triggers Usage reload
        _ = RefreshBreakdownAsync();
    }

    /// <summary>Recompute the breakdown strip once the reloaded usage has arrived
    /// (otherwise the down/up/top-app/top-host totals go stale on a range change).</summary>
    private async Task RefreshBreakdownAsync()
    {
        try { await Usage.LoadAsync(); } catch { return; }
        UpdateBreakdown();
    }

    partial void OnSubViewChanged(TrafficSubView value)
    {
        if (value == TrafficSubView.Columns) _ = Usage.LoadAsync();
    }
}
