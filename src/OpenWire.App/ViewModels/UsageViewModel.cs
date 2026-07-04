using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>Usage screen: byte totals grouped by apps / hosts / traffic type over
/// a chosen window.</summary>
public partial class UsageViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private UsageGroupBy _groupBy = UsageGroupBy.Apps;
    [ObservableProperty] private GraphRange _range = GraphRange.Day;
    [ObservableProperty] private ObservableCollection<AppUsage> _apps = new();
    [ObservableProperty] private ObservableCollection<HostUsage> _hosts = new();
    [ObservableProperty] private ObservableCollection<TrafficTypeUsage> _types = new();
    [ObservableProperty] private string _totalText = "";

    public UsageViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var u = await _client.GetUsageAsync(Range, GroupBy);
        Apps = new ObservableCollection<AppUsage>(u.Apps);
        Hosts = new ObservableCollection<HostUsage>(u.Hosts);
        Types = new ObservableCollection<TrafficTypeUsage>(u.Types);
        TotalText = $"{ByteFormatter.Bytes(u.TotalBytesIn + u.TotalBytesOut)} · " +
                    $"down {ByteFormatter.Bytes(u.TotalBytesIn)} · up {ByteFormatter.Bytes(u.TotalBytesOut)}";
    }

    partial void OnGroupByChanged(UsageGroupBy value) => _ = LoadAsync();
    partial void OnRangeChanged(GraphRange value) => _ = LoadAsync();
}
