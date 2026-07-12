using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
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

    // ---- per-app data quota ----
    public AppQuota? Quota { get; }

    /// <summary>True when the engine reports this app has reached its quota this period.</summary>
    public bool QuotaOver { get; }

    /// <summary>Column text: the limit + period, or a "+" affordance when none is set.</summary>
    public string QuotaText => Quota is null
        ? "＋"
        : $"{ByteFormatter.Bytes(Quota.LimitBytes)} · {QuotaPeriodShort(Quota.Period)}";

    public bool HasQuota => Quota is not null;

    private static string QuotaPeriodShort(QuotaPeriod p) => p switch
    {
        QuotaPeriod.Daily => Loc.S("L.Fw.QuotaDailyShort"),
        QuotaPeriod.Weekly => Loc.S("L.Fw.QuotaWeeklyShort"),
        _ => Loc.S("L.Fw.QuotaMonthlyShort"),
    };

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _blockIn;
    [ObservableProperty] private bool _blockOut;

    public AppRowVM(AppUsage usage, FirewallViewModel parent, AppFirewallRule? rule, AppQuota? quota, bool quotaOver)
    {
        Usage = usage;
        _parent = parent;
        _blockIn = rule?.BlockIncoming ?? false;
        _blockOut = rule?.BlockOutgoing ?? false;
        Quota = quota;
        QuotaOver = quotaOver;
        Processes = new ObservableCollection<ProcRowVM>(usage.Processes.Select(p => new ProcRowVM(p)));
    }

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;
    [RelayCommand] private Task ToggleIn() => _parent.ApplyAsync(this);
    [RelayCommand] private Task ToggleOut() => _parent.ApplyAsync(this);
    [RelayCommand] private void EditQuota() => _parent.RequestEditQuota(this);
}

/// <summary>One blocklist subscription row in the Firewall screen's blocklist panel.</summary>
public partial class BlocklistRowVM : ObservableObject
{
    private readonly FirewallViewModel _parent;

    public string Id { get; }
    public string Name { get; }
    public string Url { get; }
    public bool IsPreset { get; }
    public string EntryCountText { get; }
    public string LastFetchText { get; }
    public string LastError { get; }
    public bool HasError => LastError.Length > 0;

    [ObservableProperty] private bool _enabled;

    public BlocklistRowVM(FirewallViewModel parent, BlocklistSubscription sub, BlocklistStatusItem? status)
    {
        _parent = parent;
        Id = sub.Id;
        Name = sub.Name;
        Url = sub.Url;
        IsPreset = sub.IsPreset;
        _enabled = sub.Enabled;
        EntryCountText = status is { EntryCount: > 0 } ? status.EntryCount.ToString("N0") : "—";
        LastFetchText = status is { LastFetchUnix: > 0 }
            ? DateTimeOffset.FromUnixTimeSeconds(status.LastFetchUnix).ToLocalTime().ToString("MM-dd HH:mm")
            : "—";
        LastError = status?.LastError ?? "";
    }

    [RelayCommand] private Task Toggle() => _parent.SetBlocklistEnabledAsync(this);
    [RelayCommand] private Task Remove() => _parent.RemoveBlocklistAsync(this);
}

/// <summary>Firewall screen: mode selector + the per-app allow/block table.</summary>
public partial class FirewallViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private FirewallMode _mode = FirewallMode.Off;
    [ObservableProperty] private ObservableCollection<AppRowVM> _apps = new();
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canEnforce;
    [ObservableProperty] private bool _lockdownActive;
    [ObservableProperty] private string _lockdownUntilText = "";
    [ObservableProperty] private string _searchText = "";

    // Column sort: empty key = natural order (by total, as the engine returned it).
    [ObservableProperty] private string _sortKey = "";
    [ObservableProperty] private bool _sortAscending;
    private List<AppRowVM> _allApps = new();

    // Profiles + active network.
    [ObservableProperty] private ObservableCollection<FirewallProfile> _profiles = new();
    [ObservableProperty] private FirewallProfile? _selectedProfile;
    [ObservableProperty] private string _networkName = "";
    private string _networkFingerprint = "";
    private bool _suppressProfileSwitch;

    // Blocklist subscriptions panel.
    [ObservableProperty] private ObservableCollection<BlocklistRowVM> _blocklists = new();
    [ObservableProperty] private bool _blocklistsExpanded;
    [ObservableProperty] private bool _blocklistEnforce;
    [ObservableProperty] private bool _blocklistRefreshing;
    [ObservableProperty] private string _blocklistSummary = "";
    [ObservableProperty] private string _newListName = "";
    [ObservableProperty] private string _newListUrl = "";
    private bool _suppressBlocklistApply;

    public FirewallViewModel(EngineClient client) => _client = client;

    /// <summary>Raised when a row asks to edit its data quota; the View shows the modal editor.</summary>
    public event Action<AppRowVM>? EditQuotaRequested;
    public void RequestEditQuota(AppRowVM row) => EditQuotaRequested?.Invoke(row);

    /// <summary>Persist a quota change from the editor dialog (null quota = remove), then reload.</summary>
    public async Task SaveQuotaAsync(string appId, AppQuota? quota)
    {
        var settings = (await _client.GetSettingsAsync()).Settings;
        settings.AppQuotas.RemoveAll(q => q.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (quota is not null) settings.AppQuotas.Add(quota);
        await _client.SetSettingsAsync(settings);
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        var fw = await _client.GetFirewallAsync();
        Mode = fw.Status.Mode;
        CanEnforce = fw.Status.CanEnforce;
        LockdownActive = fw.Status.LockdownActive;
        LockdownUntilText = fw.Status.LockdownUntilUnix > 0
            ? string.Format(Loc.S("L.Fw.LockdownUntilFmt"),
                DateTimeOffset.FromUnixTimeSeconds(fw.Status.LockdownUntilUnix).ToLocalTime().ToString("HH:mm"))
            : "";
        NetworkName = fw.Status.NetworkName;
        _networkFingerprint = fw.Status.NetworkFingerprint;
        var rules = fw.Rules.ToDictionary(r => r.AppId, StringComparer.OrdinalIgnoreCase);

        _suppressProfileSwitch = true;
        Profiles = new ObservableCollection<FirewallProfile>(fw.Profiles);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(fw.Status.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                          ?? Profiles.FirstOrDefault();
        _suppressProfileSwitch = false;

        var settings = (await _client.GetSettingsAsync()).Settings;
        var quotas = settings.AppQuotas.ToDictionary(q => q.AppId, StringComparer.OrdinalIgnoreCase);
        var over = new HashSet<string>(fw.Status.QuotaExceededAppIds, StringComparer.OrdinalIgnoreCase);

        var usage = await _client.GetUsageAsync(GraphRange.Day, UsageGroupBy.Apps);
        _allApps = usage.Apps.Select(a => new AppRowVM(
            a, this, rules.GetValueOrDefault(a.App.Id),
            quotas.GetValueOrDefault(a.App.Id), over.Contains(a.App.Id))).ToList();
        ApplyAppFilter();

        StatusText = CanEnforce
            ? $"{fw.Status.BlockedAppCount} blocked · {_allApps.Count} active applications · profile “{fw.Status.ActiveProfile}”"
            : "Run the engine as administrator to enforce blocks and show per-app rates";

        await LoadBlocklistsAsync();
    }

    // ---- blocklist subscriptions ----

    public async Task LoadBlocklistsAsync()
    {
        var settings = (await _client.GetSettingsAsync()).Settings;
        var status = await _client.GetBlocklistStatusAsync();
        var byId = status.Lists.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);

        _suppressBlocklistApply = true;
        BlocklistEnforce = settings.BlocklistEnforce;
        _suppressBlocklistApply = false;

        BlocklistRefreshing = status.Refreshing;
        Blocklists = new ObservableCollection<BlocklistRowVM>(
            settings.Blocklists.Select(b => new BlocklistRowVM(this, b, byId.GetValueOrDefault(b.Id))));

        int enabledCount = settings.Blocklists.Count(b => b.Enabled);
        long entries = status.Lists.Where(l => l.Enabled).Sum(l => (long)l.EntryCount);
        BlocklistSummary = string.Format(Loc.S("L.Fw.BlocklistSummaryFmt"),
            enabledCount, entries.ToString("N0"), status.BlockedAddressCount);
    }

    /// <summary>Load-modify-save round trip for the blocklist part of the settings, then
    /// re-read so the panel reflects what the engine actually accepted.</summary>
    private async Task MutateSettingsAsync(Action<AppSettings> mutate)
    {
        var settings = (await _client.GetSettingsAsync()).Settings;
        mutate(settings);
        await _client.SetSettingsAsync(settings);
        await LoadBlocklistsAsync();
    }

    public Task SetBlocklistEnabledAsync(BlocklistRowVM row) => MutateSettingsAsync(s =>
    {
        var sub = s.Blocklists.FirstOrDefault(b => b.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        if (sub is not null) sub.Enabled = row.Enabled;
    });

    public Task RemoveBlocklistAsync(BlocklistRowVM row) => row.IsPreset
        ? Task.CompletedTask
        : MutateSettingsAsync(s => s.Blocklists.RemoveAll(b => b.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)));

    partial void OnBlocklistEnforceChanged(bool value)
    {
        if (_suppressBlocklistApply) return;
        _ = MutateSettingsAsync(s => s.BlocklistEnforce = value);
    }

    [RelayCommand]
    private async Task AddBlocklist()
    {
        string name = NewListName.Trim();
        string url = NewListUrl.Trim();
        if (name.Length == 0
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return;

        await MutateSettingsAsync(s => s.Blocklists.Add(new BlocklistSubscription
        {
            Id = "user-" + Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Url = url,
            Enabled = true,
            IsPreset = false,
        }));
        NewListName = "";
        NewListUrl = "";
    }

    [RelayCommand]
    private async Task RefreshBlocklists()
    {
        await _client.RefreshBlocklistsAsync();
        BlocklistRefreshing = true;
        // Give the download a moment, then re-read; slow lists finish in the background
        // and show up on the next visit or manual refresh.
        await Task.Delay(1500);
        await LoadBlocklistsAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyAppFilter();

    /// <summary>Sort by a column; clicking the active column again reverses it.
    /// Sorting happens on click (and survives reloads) — not on every live tick,
    /// so rows don't jump around while rates update.</summary>
    [RelayCommand]
    private void SortBy(string key)
    {
        if (string.Equals(key, SortKey, StringComparison.Ordinal))
            SortAscending = !SortAscending;
        else
        {
            SortKey = key;
            // Text columns default A→Z; rate/state columns default high→low.
            SortAscending = key is "name" or "version" or "host";
        }
        ApplyAppFilter();
    }

    private void ApplyAppFilter()
    {
        IEnumerable<AppRowVM> q = _allApps;
        var s = SearchText?.Trim();
        if (!string.IsNullOrEmpty(s))
            q = q.Where(a => (a.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (a.HostText?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (a.Path?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        q = ApplySort(q);
        Apps = new ObservableCollection<AppRowVM>(q);
    }

    private IEnumerable<AppRowVM> ApplySort(IEnumerable<AppRowVM> q)
    {
        if (string.IsNullOrEmpty(SortKey)) return q;
        Func<AppRowVM, object> key = SortKey switch
        {
            "name" => a => a.Name ?? "",
            "in" => a => a.BlockIn,           // blocked sorts before allowed when descending
            "out" => a => a.BlockOut,
            "version" => a => a.Usage.App.Version ?? "",
            "host" => a => a.Usage.PrimaryHost ?? "",
            "vt" => a => (int)a.RepState,
            "trend" => a => a.Usage.DownRate + a.Usage.UpRate,
            "down" => a => a.Usage.DownRate,
            "up" => a => a.Usage.UpRate,
            _ => a => a.Name ?? "",
        };
        var cmp = SortKey is "name" or "version" or "host"
            ? (IComparer<object>)StringKeyComparer.Instance
            : Comparer<object>.Default;
        return SortAscending ? q.OrderBy(key, cmp) : q.OrderByDescending(key, cmp);
    }

    private sealed class StringKeyComparer : IComparer<object>
    {
        public static readonly StringKeyComparer Instance = new();
        public int Compare(object? x, object? y) =>
            string.Compare(x as string, y as string, StringComparison.OrdinalIgnoreCase);
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
        if (mode == FirewallMode.Off) LockdownActive = false;   // the engine lifts lock-down when disabled
    }

    /// <summary>Engage or lift the global network lock-down (block every app), then reload to reflect it.</summary>
    [RelayCommand]
    private async Task ToggleLockdown()
    {
        if (!CanEnforce) return;
        await _client.SetLockdownAsync(!LockdownActive);
        await LoadAsync();
    }

    /// <summary>Engage a timed "panic" lock-down for the given number of minutes; it auto-lifts.</summary>
    [RelayCommand]
    private async Task PanicFor(string minutes)
    {
        if (!CanEnforce || !int.TryParse(minutes, out int m) || m <= 0) return;
        await _client.SetLockdownAsync(true, m * 60L);
        await LoadAsync();
    }
}
