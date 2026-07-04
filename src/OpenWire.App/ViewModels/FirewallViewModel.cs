using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>One application row on the Firewall screen (separate In/Out control).</summary>
public partial class AppRowVM : ObservableObject
{
    private readonly FirewallViewModel _parent;

    public AppUsage Usage { get; }
    public string AppId => Usage.App.Id;
    public string Name => Usage.App.Name;
    public string Path => Usage.App.ExecutablePath;
    public string Version => string.IsNullOrEmpty(Usage.App.Version) ? "—" : Usage.App.Version;
    public string DownText => ByteFormatter.Bytes(Usage.BytesIn);
    public string UpText => ByteFormatter.Bytes(Usage.BytesOut);
    public string HostText => Usage.HostCount > 0 ? $"{Usage.HostCount} host(s)" : $"{Usage.ActiveConnections} conn.";

    [ObservableProperty] private bool _blockIn;
    [ObservableProperty] private bool _blockOut;

    public AppRowVM(AppUsage usage, FirewallViewModel parent, AppFirewallRule? rule)
    {
        Usage = usage;
        _parent = parent;
        _blockIn = rule?.BlockIncoming ?? false;
        _blockOut = rule?.BlockOutgoing ?? false;
    }

    [RelayCommand] private Task ToggleIn() => _parent.ApplyAsync(this);
    [RelayCommand] private Task ToggleOut() => _parent.ApplyAsync(this);
}

/// <summary>Firewall screen: mode selector + per-app allow/block table.</summary>
public partial class FirewallViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private FirewallMode _mode = FirewallMode.Off;
    [ObservableProperty] private ObservableCollection<AppRowVM> _apps = new();
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canEnforce;

    public FirewallViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var fw = await _client.GetFirewallAsync();
        Mode = fw.Status.Mode;
        CanEnforce = fw.Status.CanEnforce;
        var rules = fw.Rules.ToDictionary(r => r.AppId, StringComparer.OrdinalIgnoreCase);

        var usage = await _client.GetUsageAsync(GraphRange.Day, UsageGroupBy.Apps);
        Apps = new ObservableCollection<AppRowVM>(
            usage.Apps.Select(a => new AppRowVM(a, this, rules.GetValueOrDefault(a.App.Id))));

        StatusText = CanEnforce
            ? $"{fw.Status.BlockedAppCount} blocked · {Apps.Count} apps"
            : "Run the engine as administrator to enforce blocks";
    }

    /// <summary>Push a row's current In/Out block state to the firewall.</summary>
    public async Task ApplyAsync(AppRowVM row)
    {
        if (!CanEnforce) return;
        await _client.SetAppBlockedAsync(row.AppId, row.Path, row.BlockIn, row.BlockOut);
    }

    [RelayCommand]
    private async Task SetMode(FirewallMode mode)
    {
        await _client.SetFirewallModeAsync(mode);
        Mode = mode;
    }
}
