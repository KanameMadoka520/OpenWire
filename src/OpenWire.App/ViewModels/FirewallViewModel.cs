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

    /// <summary>ISO country code of the most-contacted host, for the flag icon.</summary>
    public string HostCountry => Usage.PrimaryHostCountry;

    /// <summary>Recent per-second throughput history driving the row's sparkline.</summary>
    public IReadOnlyList<int> Spark => Usage.RateHistory;

    // ---- VirusTotal reputation (optional; empty when no API key is configured) ----
    public ReputationState RepState => Usage.Reputation?.State ?? ReputationState.Unknown;

    public string ReputationText => Usage.Reputation is not { } r ? "" : r.State switch
    {
        ReputationState.Scanning => "···",
        ReputationState.Clean => "clean",
        ReputationState.Flagged => $"{r.Malicious}/{r.Total}",
        ReputationState.NotFound => "unlisted",
        ReputationState.Error => "n/a",
        _ => "",
    };

    public string ReputationTip => Usage.Reputation is not { } r ? "" : r.State switch
    {
        ReputationState.Scanning => "Checking VirusTotal…",
        ReputationState.Clean => $"VirusTotal: clean · {r.Total} engines\n{r.Sha256}",
        ReputationState.Flagged => $"VirusTotal: {r.Malicious} of {r.Total} engines flagged this file\n{r.Sha256}",
        ReputationState.NotFound => "Not present in VirusTotal's dataset",
        ReputationState.Error => $"VirusTotal: {r.Detail}",
        _ => "",
    };

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
    [ObservableProperty] private string _searchText = "";
    private List<AppRowVM> _allApps = new();

    // Profiles + active network.
    [ObservableProperty] private ObservableCollection<FirewallProfile> _profiles = new();
    [ObservableProperty] private FirewallProfile? _selectedProfile;
    [ObservableProperty] private string _networkName = "";
    private string _networkFingerprint = "";
    private bool _suppressProfileSwitch;

    public FirewallViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var fw = await _client.GetFirewallAsync();
        Mode = fw.Status.Mode;
        CanEnforce = fw.Status.CanEnforce;
        NetworkName = fw.Status.NetworkName;
        _networkFingerprint = fw.Status.NetworkFingerprint;
        var rules = fw.Rules.ToDictionary(r => r.AppId, StringComparer.OrdinalIgnoreCase);

        _suppressProfileSwitch = true;
        Profiles = new ObservableCollection<FirewallProfile>(fw.Profiles);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(fw.Status.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                          ?? Profiles.FirstOrDefault();
        _suppressProfileSwitch = false;

        var usage = await _client.GetUsageAsync(GraphRange.Day, UsageGroupBy.Apps);
        _allApps = usage.Apps.Select(a => new AppRowVM(a, this, rules.GetValueOrDefault(a.App.Id))).ToList();
        ApplyAppFilter();

        StatusText = CanEnforce
            ? $"{fw.Status.BlockedAppCount} blocked · {_allApps.Count} active applications · profile “{fw.Status.ActiveProfile}”"
            : "Run the engine as administrator to enforce blocks and show per-app rates";
    }

    partial void OnSearchTextChanged(string value) => ApplyAppFilter();

    private void ApplyAppFilter()
    {
        IEnumerable<AppRowVM> q = _allApps;
        var s = SearchText?.Trim();
        if (!string.IsNullOrEmpty(s))
            q = q.Where(a => (a.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (a.HostText?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (a.Path?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        Apps = new ObservableCollection<AppRowVM>(q);
    }

    partial void OnSelectedProfileChanged(FirewallProfile? value)
    {
        if (_suppressProfileSwitch || value is null) return;
        _ = ActivateAsync(value.Name);
    }

    private async Task ActivateAsync(string name)
    {
        await _client.ActivateFirewallProfileAsync(name);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        var taken = new HashSet<string>(Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        string name = "Profile";
        for (int i = 2; taken.Contains(name); i++) name = $"Profile {i}";

        var prof = new FirewallProfile
        {
            Name = name,
            Mode = Mode,
            NetworkLabel = NetworkName,
            AutoActivateOnNetwork = _networkFingerprint, // bind to the current network
            BlockedAppIds = Apps.Where(a => a.BlockIn || a.BlockOut).Select(a => a.AppId).ToList(),
        };
        await _client.SaveFirewallProfileAsync(prof);
        await _client.ActivateFirewallProfileAsync(name);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        var name = SelectedProfile?.Name;
        if (string.IsNullOrEmpty(name) || name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return;
        await _client.DeleteFirewallProfileAsync(name);
        await LoadAsync();
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
