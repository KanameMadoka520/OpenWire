using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.App.Util;
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

    // The top app/host, split from the "+N more" affordance so the overflow can open a popup.
    [ObservableProperty] private string _topAppName = "";
    [ObservableProperty] private string _topHostName = "";
    [ObservableProperty] private string _moreAppsText = "";
    [ObservableProperty] private string _moreHostsText = "";
    [ObservableProperty] private bool _hasMoreApps;
    [ObservableProperty] private bool _hasMoreHosts;

    // The time range shown at the graph's corner — the drag-selected band when selecting,
    // otherwise the currently-visible window.
    [ObservableProperty] private string _rangeLabel = "";
    private bool _selectionActive;
    private string _viewRangeText = "";

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

    /// <summary>Show the drag-selected band's totals in the breakdown bar.</summary>
    public void ApplySelection(double fromSec, double toSec, double downBytes, double upBytes)
    {
        _selectionActive = true;
        BreakdownDown = ByteFormatter.Bytes((long)downBytes);
        BreakdownUp = ByteFormatter.Bytes((long)upBytes);
        RangeLabel = FormatRange(fromSec, toSec);
    }

    /// <summary>Back to whole-view totals once the band is dismissed.</summary>
    public void ClearSelection()
    {
        if (!_selectionActive) return;
        _selectionActive = false;
        RangeLabel = _viewRangeText;
        UpdateBreakdown();
    }

    /// <summary>The graph's currently-visible time window, shown at the graph corner when no
    /// band is selected.</summary>
    public void SetViewRange(double fromSec, double toSec)
    {
        _viewRangeText = FormatRange(fromSec, toSec);
        if (!_selectionActive) RangeLabel = _viewRangeText;
    }

    private static string FormatRange(double fromSec, double toSec)
    {
        // Month name in the app's language (not the OS locale); the 24h clock is culture-neutral.
        var c = System.Globalization.CultureInfo.GetCultureInfo(LangManager.CultureName(LangManager.Current));
        var f = DateTimeOffset.FromUnixTimeSeconds((long)fromSec).ToLocalTime();
        var t = DateTimeOffset.FromUnixTimeSeconds((long)toSec).ToLocalTime();
        return f.Date == t.Date
            ? $"{f.ToString("MMM d", c)}  {f:HH:mm} – {t:HH:mm}"
            : $"{f.ToString("MMM d HH:mm", c)} – {t.ToString("MMM d HH:mm", c)}";
    }

    private void UpdateBreakdown()
    {
        if (_selectionActive) return; // a reload must not stomp the band's totals
        var apps = Usage.Apps;
        var hosts = Usage.Hosts;
        BreakdownDown = ByteFormatter.Bytes(apps.Sum(a => a.BytesIn));
        BreakdownUp = ByteFormatter.Bytes(apps.Sum(a => a.BytesOut));

        TopAppName = apps.Count > 0 ? apps[0].App.Name : "";
        HasMoreApps = apps.Count > 1;
        MoreAppsText = HasMoreApps ? string.Format(Loc.S("L.Traffic.MoreFmt"), apps.Count - 1) : "";
        TopHostName = hosts.Count > 0 ? hosts[0].Host : "";
        HasMoreHosts = hosts.Count > 1;
        MoreHostsText = HasMoreHosts ? string.Format(Loc.S("L.Traffic.MoreFmt"), hosts.Count - 1) : "";

        // Kept for any legacy binding; the split name + "+N more" above is what the bar shows.
        TopApp = TopAppName + (HasMoreApps ? "  " + MoreAppsText : "");
        TopHost = TopHostName + (HasMoreHosts ? "  " + MoreHostsText : "");
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
