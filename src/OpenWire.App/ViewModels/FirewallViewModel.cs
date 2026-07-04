using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>A child OS process shown under an app in the firewall tree.</summary>
public sealed class ProcRowVM
{
    public int Pid { get; }
    public string DownText { get; }
    public string UpText { get; }

    public ProcRowVM(AppProcess p)
    {
        Pid = p.Pid;
        DownText = p.DownRate > 0 ? ByteFormatter.Rate(p.DownRate) : "";
        UpText = p.UpRate > 0 ? ByteFormatter.Rate(p.UpRate) : "";
    }
}

/// <summary>One application row on the Firewall screen (separate In/Out control,
/// resolved host, live rates, and an expandable child-process list).</summary>
public partial class AppRowVM : ObservableObject
{
    private readonly FirewallViewModel _parent;

    public AppUsage Usage { get; }
    public string AppId => Usage.App.Id;
    public string Name => Usage.App.Name;
    public string Path => Usage.App.ExecutablePath;
    public string Version => string.IsNullOrEmpty(Usage.App.Version) ? "—" : Usage.App.Version;
    public string DownText => Usage.DownRate > 0 ? ByteFormatter.Rate(Usage.DownRate) : "";
    public string UpText => Usage.UpRate > 0 ? ByteFormatter.Rate(Usage.UpRate) : "";

    public string HostText
    {
        get
        {
            if (string.IsNullOrEmpty(Usage.PrimaryHost)) return $"{Usage.ActiveConnections} conn.";
            int more = Math.Max(0, Usage.HostCount - 1);
            return more > 0 ? $"{Usage.PrimaryHost}  +{more} more" : Usage.PrimaryHost;
        }
    }

    public ObservableCollection<ProcRowVM> Processes { get; }
    public bool HasChildren => Processes.Count > 0;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _blockIn;
    [ObservableProperty] private bool _blockOut;

    public AppRowVM(AppUsage usage, FirewallViewModel parent, AppFirewallRule? rule)
    {
        Usage = usage;
        _parent = parent;
        _blockIn = rule?.BlockIncoming ?? false;
        _blockOut = rule?.BlockOutgoing ?? false;
        Processes = new ObservableCollection<ProcRowVM>(usage.Processes.Select(p => new ProcRowVM(p)));
    }

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;
    [RelayCommand] private Task ToggleIn() => _parent.ApplyAsync(this);
    [RelayCommand] private Task ToggleOut() => _parent.ApplyAsync(this);
}

/// <summary>Firewall screen: mode selector + the per-app allow/block table.</summary>
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
            ? $"{fw.Status.BlockedAppCount} blocked · {Apps.Count} active applications"
            : "Run the engine as administrator to enforce blocks and show per-app rates";
    }

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
