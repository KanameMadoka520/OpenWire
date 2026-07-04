using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>One application row on the Firewall screen.</summary>
public partial class AppRowVM : ObservableObject
{
    private readonly FirewallViewModel _parent;

    public AppUsage Usage { get; }
    public string AppId => Usage.App.Id;
    public string Name => Usage.App.Name;
    public string Path => Usage.App.ExecutablePath;
    public string Publisher => string.IsNullOrEmpty(Usage.App.Publisher) ? "Unsigned" : Usage.App.Publisher;
    public string DownText => ByteFormatter.Bytes(Usage.BytesIn);
    public string UpText => ByteFormatter.Bytes(Usage.BytesOut);
    public int Connections => Usage.ActiveConnections;

    [ObservableProperty] private AppFirewallStatus _status;

    public bool IsBlocked => Status == AppFirewallStatus.Blocked;

    public AppRowVM(AppUsage usage, FirewallViewModel parent)
    {
        Usage = usage;
        _parent = parent;
        _status = usage.FirewallStatus;
    }

    partial void OnStatusChanged(AppFirewallStatus value) => OnPropertyChanged(nameof(IsBlocked));

    [RelayCommand]
    private Task ToggleBlock() => _parent.ToggleBlockAsync(this);
}

/// <summary>Firewall screen: mode selector + the per-app allow/block table.</summary>
public partial class FirewallViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private FirewallMode _mode = FirewallMode.Off;
    [ObservableProperty] private ObservableCollection<AppRowVM> _apps = new();
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canEnforce;
    [ObservableProperty] private string _search = "";

    public FirewallViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var fw = await _client.GetFirewallAsync();
        Mode = fw.Status.Mode;
        CanEnforce = fw.Status.CanEnforce;

        var usage = await _client.GetUsageAsync(GraphRange.Day, UsageGroupBy.Apps);
        Apps = new ObservableCollection<AppRowVM>(usage.Apps.Select(a => new AppRowVM(a, this)));

        StatusText = CanEnforce
            ? $"{fw.Status.BlockedAppCount} blocked · {Apps.Count} apps"
            : "Run the engine as administrator to enforce blocks";
    }

    public async Task ToggleBlockAsync(AppRowVM row)
    {
        if (!CanEnforce) return;
        bool block = !row.IsBlocked;
        await _client.SetAppBlockedAsync(row.AppId, row.Path, block, block);
        row.Status = block ? AppFirewallStatus.Blocked : AppFirewallStatus.Allowed;
    }

    [RelayCommand]
    private async Task SetMode(FirewallMode mode)
    {
        await _client.SetFirewallModeAsync(mode);
        Mode = mode;
    }
}
