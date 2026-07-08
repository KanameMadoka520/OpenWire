using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Ipc;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>Settings screen: monitors, data plan, resolution and firewall options.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly EngineClient _client;
    private AppSettings _settings = new();
    private long _dataPlanUsedBytes;              // last-known consumption from EngineStatus.DataPlan
    private const double DataPlanBarMax = 300;    // px reference width for the usage bar

    [ObservableProperty] private bool _resolveHostNames = true;
    [ObservableProperty] private bool _resolveGeoIp = true;

    // ---- Launch at logon (elevated Task Scheduler task) ----
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private bool _canAutoStart = true;   // false disables the toggle for non-admins
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoStartStatus))]
    private string _autoStartStatus = "";
    public bool HasAutoStartStatus => !string.IsNullOrEmpty(AutoStartStatus);
    private bool _suppressAutoStartApply;                     // don't fire IPC while reconciling on load

    // ---- GeoIP database (source + build date + in-app update) ----
    [ObservableProperty] private string _geoIpStatusText = "";   // "DB-IP Lite · 2026-07-01"
    [ObservableProperty] private string _geoIpUpdatedText = "";  // "Last checked 2026-07-06" / ""
    [ObservableProperty] private string _geoIpResultText = "";   // transient result of an update
    [ObservableProperty] private bool _geoIpAutoUpdate;
    [ObservableProperty] private bool _isUpdatingGeoIp;
    [ObservableProperty] private bool _monitorNewDevices = true;
    [ObservableProperty] private bool _monitorDnsChanges = true;
    [ObservableProperty] private bool _monitorRdp = true;
    [ObservableProperty] private bool _monitorUsageAnomalies = true;
    [ObservableProperty] private bool _monitorHostsFile = true;
    [ObservableProperty] private bool _monitorArpSpoofing = true;
    [ObservableProperty] private bool _monitorProxyChanges = true;
    [ObservableProperty] private int _historyRetentionDays = 90;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _dataPlanEnabled;
    [ObservableProperty] private double _dataLimitGb = 100;
    [ObservableProperty] private int _billingDay = 1;
    [ObservableProperty] private int _warnAtPercent = 90;
    // Live data-plan usage meter, populated from EngineStatus.DataPlan on load.
    [ObservableProperty] private bool _hasDataPlanUsage;
    [ObservableProperty] private double _dataPlanBarWidth;
    [ObservableProperty] private string _dataPlanUsedText = "";
    [ObservableProperty] private string _dataPlanRemainingText = "";
    [ObservableProperty] private bool _dataPlanNearLimit;
    [ObservableProperty] private bool _dataPlanOverLimit;
    [ObservableProperty] private string _virusTotalApiKey = "";
    [ObservableProperty] private string _engineInfo = "";
    [ObservableProperty] private string _savedText = "";

    // ---- Storage / database (relocatable DB + space used + clear history) ----
    [ObservableProperty] private string _storageLocation = "";
    [ObservableProperty] private string _storageSizeText = "";
    [ObservableProperty] private string _storageDetailText = "";

    /// <summary>Non-empty only after a relocation that needs an engine restart to take effect.</summary>
    [ObservableProperty] private string _restartNote = "";

    /// <summary>Shown in the About card (assembly version, set via Directory.Build.props).</summary>
    public string AppVersion { get; } =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>UI skin (Minimal / Pencil / BerryDay / BerryNight). Live-switched.</summary>
    [ObservableProperty] private string _theme = ThemeManager.Read();

    /// <summary>UI language (English / SimplifiedChinese / TraditionalChinese). Live-switched.</summary>
    [ObservableProperty] private string _language = LangManager.Read();

    /// <summary>Raised after settings are persisted, so the shell can react live
    /// (tray notifications / minimize-to-tray preferences).</summary>
    public event Action<AppSettings>? Saved;

    public SettingsViewModel(EngineClient client) => _client = client;

    // Fired only when the user actually picks a different skin/language (EnumMatchConverter.
    // ConvertBack never writes on the initial source→target binding sync).
    partial void OnThemeChanged(string value) => ThemeManager.Switch(value);

    partial void OnLanguageChanged(string value) => LangManager.Switch(value);

    public async Task LoadAsync()
    {
        var s = (await _client.GetSettingsAsync()).Settings;
        _settings = s;
        ResolveHostNames = s.ResolveHostNames;
        ResolveGeoIp = s.ResolveGeoIp;
        MonitorNewDevices = s.MonitorNewDevices;
        MonitorDnsChanges = s.MonitorDnsChanges;
        MonitorRdp = s.MonitorRdp;
        MonitorUsageAnomalies = s.MonitorUsageAnomalies;
        MonitorHostsFile = s.MonitorHostsFile;
        MonitorArpSpoofing = s.MonitorArpSpoofing;
        MonitorProxyChanges = s.MonitorProxyChanges;
        HistoryRetentionDays = s.HistoryRetentionDays;
        ShowNotifications = s.ShowDesktopNotifications;
        MinimizeToTray = s.MinimizeToTray;
        DataPlanEnabled = s.DataPlan.Enabled;
        DataLimitGb = s.DataPlan.LimitBytes > 0 ? s.DataPlan.LimitBytes / (1024.0 * 1024 * 1024) : 100;
        BillingDay = s.DataPlan.BillingCycleStartDay;
        WarnAtPercent = (int)Math.Round(Math.Clamp(s.DataPlan.WarnAtFraction, 0.1, 1.0) * 100);
        VirusTotalApiKey = s.VirusTotalApiKey;
        GeoIpAutoUpdate = s.GeoIpAutoUpdate;

        try { _dataPlanUsedBytes = (await _client.GetStatusAsync()).Status.DataPlan.UsedBytes; } catch { /* engine busy */ }
        UpdateDataPlanMeter();

        try { ApplyGeoStatus(await _client.GetGeoIpStatusAsync()); } catch { /* engine busy */ }

        // Reconcile the launch-at-logon toggle against the real scheduled task, not the persisted bool.
        CanAutoStart = UserCanElevate();
        try
        {
            var auto = await _client.GetAutoStartAsync();
            _suppressAutoStartApply = true;
            LaunchOnStartup = auto.Exists && auto.Enabled;
            _suppressAutoStartApply = false;
            AutoStartStatus = CanAutoStart ? "" : Loc.S("L.Set.AutoStartNeedsAdmin");
        }
        catch { _suppressAutoStartApply = false; }

        var hello = await _client.HelloAsync();
        EngineInfo = string.Format(Loc.S("L.Set.EngineInfoFmt"),
            hello.EngineVersion, hello.MachineName,
            hello.CanEnforceFirewall ? Loc.S("L.Set.FwEnabled") : Loc.S("L.Set.FwUnavailable"),
            hello.GeoIpAvailable ? Loc.S("L.Set.GeoLoaded") : Loc.S("L.Set.GeoNotInstalled"));

        await LoadStorageAsync();
    }

    /// <summary>Fetches the current database location/size and mirrors it into the STORAGE card.</summary>
    public async Task LoadStorageAsync()
    {
        var resp = await _client.GetStorageInfoAsync();
        if (resp.Storage is { } s) ApplyStorage(s);
    }

    private void ApplyStorage(StorageInfo s)
    {
        StorageLocation = s.DataDirectory;
        StorageSizeText = ByteFormatter.Bytes(s.DatabaseBytes);

        StorageDetailText = s.OldestRecord is { } oldest
            ? string.Format(Loc.S("L.Set.StorageDetailFmt"),
                ByteFormatter.Bytes(s.FreeBytes),
                oldest.LocalDateTime.ToString(Loc.S("L.Set.StorageDateFormat")))
            : string.Format(Loc.S("L.Set.StorageFreeFmt"), ByteFormatter.Bytes(s.FreeBytes));

        RestartNote = s.RestartRequired ? Loc.S("L.Set.StorageRestartNote") : "";
    }

    /// <summary>Pick a new folder for the database (guards cancel) and relocate it.</summary>
    [RelayCommand]
    private async Task ChangeLocation()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.S("L.Set.StorageChooseFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(StorageLocation) && System.IO.Directory.Exists(StorageLocation))
            dlg.SelectedPath = StorageLocation;

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var chosen = dlg.SelectedPath;
        if (string.IsNullOrWhiteSpace(chosen)) return;

        var resp = await _client.SetStorageLocationAsync(chosen);
        if (resp.Storage is { } s) ApplyStorage(s);
    }

    /// <summary>Drop per-minute history but keep daily rollups + settings, then refresh the size.</summary>
    [RelayCommand]
    private async Task ClearMinutes()
    {
        var resp = await _client.ClearDataAsync(ClearDataMode.MinuteHistory);
        if (resp.Storage is { } s) ApplyStorage(s);
    }

    /// <summary>Wipe all history (destructive) after an explicit confirmation, then refresh the size.</summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        var confirm = System.Windows.MessageBox.Show(
            Loc.S("L.Set.StorageClearAllConfirm"),
            Loc.S("L.Set.StorageClearAll"),
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        var resp = await _client.ClearDataAsync(ClearDataMode.AllHistory);
        if (resp.Storage is { } s) ApplyStorage(s);
    }

    // Toggling the checkbox applies immediately (create/delete the logon task), not on Save.
    partial void OnLaunchOnStartupChanged(bool value)
    {
        if (_suppressAutoStartApply) return;
        _ = ApplyAutoStartAsync(value);
    }

    private async Task ApplyAutoStartAsync(bool enabled)
    {
        if (enabled && !CanAutoStart)
        {
            SetToggle(false);
            AutoStartStatus = Loc.S("L.Set.AutoStartNeedsAdmin");
            return;
        }
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var st = await _client.SetAutoStartAsync(enabled, Environment.ProcessPath ?? "", id.Name);
            bool taskOn = st.Exists && st.Enabled;
            SetToggle(taskOn);                 // reconcile the checkbox to the real task, not the request
            AutoStartStatus = taskOn == enabled
                ? (enabled ? Loc.S("L.Set.AutoStartOn") : "")
                : string.Format(Loc.S("L.Set.AutoStartFailed"), st.Message ?? ""); // create/delete didn't take
        }
        catch (Exception ex)
        {
            try { var q = await _client.GetAutoStartAsync(); SetToggle(q.Exists && q.Enabled); } catch { }
            AutoStartStatus = string.Format(Loc.S("L.Set.AutoStartFailed"), ex.Message);
        }
    }

    /// <summary>Set the toggle without re-triggering the apply handler (used for reconciliation).</summary>
    private void SetToggle(bool on)
    {
        _suppressAutoStartApply = true;
        LaunchOnStartup = on;
        _suppressAutoStartApply = false;
    }

    /// <summary>Whether the current user is a member of the local Administrators group — true even
    /// for a non-elevated admin (the group is present as deny-only in the filtered token), which is
    /// exactly what a HIGHEST-privileges logon task needs.</summary>
    private static bool UserCanElevate()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return id.Groups?.Contains(admins) ?? false;
        }
        catch { return true; } // never block a real admin because the check itself failed
    }

    /// <summary>Maps a GeoIP status response into the card's display fields.</summary>
    private void ApplyGeoStatus(GeoIpStatusResponse st)
    {
        _settings.GeoIpLastUpdateUnix = st.LastUpdateUnix; // keep our copy in sync so Save won't stale it
        GeoIpAutoUpdate = st.AutoUpdate;

        GeoIpStatusText = !st.Available
            ? Loc.S("L.Set.GeoNotInstalled")
            : string.IsNullOrEmpty(st.BuildDate) ? st.Source : $"{st.Source} · {st.BuildDate}";

        GeoIpUpdatedText = st.LastUpdateUnix > 0
            ? string.Format(Loc.S("L.Set.GeoLastChecked"),
                DateTimeOffset.FromUnixTimeSeconds(st.LastUpdateUnix).LocalDateTime.ToString(Loc.S("L.Set.StorageDateFormat")))
            : "";
    }

    partial void OnIsUpdatingGeoIpChanged(bool value) => UpdateGeoIpCommand.NotifyCanExecuteChanged();

    private bool CanUpdateGeoIp => !IsUpdatingGeoIp;

    /// <summary>Ask the engine to download + install the latest free country database.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateGeoIp))]
    private async Task UpdateGeoIp()
    {
        IsUpdatingGeoIp = true;
        GeoIpResultText = Loc.S("L.Set.GeoUpdating");
        try
        {
            var st = await _client.UpdateGeoIpAsync();
            ApplyGeoStatus(st);
            GeoIpResultText = st.Success
                ? (st.Updated ? string.Format(Loc.S("L.Set.GeoUpdatedTo"), st.BuildDate) : Loc.S("L.Set.GeoAlreadyLatest"))
                : string.Format(Loc.S("L.Set.GeoUpdateFailed"), st.Message);
        }
        catch (Exception ex)
        {
            GeoIpResultText = string.Format(Loc.S("L.Set.GeoUpdateFailed"), ex.Message);
        }
        finally
        {
            IsUpdatingGeoIp = false;
        }
    }

    partial void OnDataPlanEnabledChanged(bool value) => UpdateDataPlanMeter();
    partial void OnDataLimitGbChanged(double value) => UpdateDataPlanMeter();
    partial void OnWarnAtPercentChanged(int value) => UpdateDataPlanMeter();
    partial void OnBillingDayChanged(int value) => UpdateDataPlanMeter();

    /// <summary>Recompute the data-plan usage meter from the last-known consumption and the current
    /// limit / warn inputs. Consumption is read from EngineStatus on load; the meter re-renders live
    /// as the user edits the limit or warn threshold.</summary>
    private void UpdateDataPlanMeter()
    {
        long limitBytes = DataLimitGb > 0 ? (long)(DataLimitGb * 1024 * 1024 * 1024) : 0;
        HasDataPlanUsage = DataPlanEnabled && limitBytes > 0;
        if (!HasDataPlanUsage)
        {
            DataPlanBarWidth = 0;
            DataPlanUsedText = DataPlanRemainingText = "";
            DataPlanNearLimit = DataPlanOverLimit = false;
            return;
        }
        double frac = Math.Min(1.0, (double)_dataPlanUsedBytes / limitBytes);
        double warnFrac = Math.Clamp(WarnAtPercent / 100.0, 0.1, 1.0);
        DataPlanBarWidth = frac * DataPlanBarMax;
        DataPlanOverLimit = _dataPlanUsedBytes >= limitBytes;
        DataPlanNearLimit = !DataPlanOverLimit && frac >= warnFrac;
        long remaining = Math.Max(0, limitBytes - _dataPlanUsedBytes);
        DataPlanUsedText = string.Format(Loc.S("L.Set.DataPlanUsedFmt"),
            ByteFormatter.Bytes(_dataPlanUsedBytes), ByteFormatter.Bytes(limitBytes));
        DataPlanRemainingText = string.Format(Loc.S("L.Set.DataPlanRemainingFmt"),
            ByteFormatter.Bytes(remaining), Math.Clamp(BillingDay, 1, 28));
    }

    [RelayCommand]
    private async Task Save()
    {
        _settings.ResolveHostNames = ResolveHostNames;
        _settings.ResolveGeoIp = ResolveGeoIp;
        _settings.MonitorNewDevices = MonitorNewDevices;
        _settings.MonitorDnsChanges = MonitorDnsChanges;
        _settings.MonitorRdp = MonitorRdp;
        _settings.MonitorUsageAnomalies = MonitorUsageAnomalies;
        _settings.MonitorHostsFile = MonitorHostsFile;
        _settings.MonitorArpSpoofing = MonitorArpSpoofing;
        _settings.MonitorProxyChanges = MonitorProxyChanges;
        _settings.HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 0, 3650);
        _settings.ShowDesktopNotifications = ShowNotifications;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.DataPlan.Enabled = DataPlanEnabled;
        _settings.DataPlan.LimitBytes = (long)(DataLimitGb * 1024 * 1024 * 1024);
        _settings.DataPlan.BillingCycleStartDay = Math.Clamp(BillingDay, 1, 28);
        _settings.DataPlan.WarnAtFraction = Math.Clamp(WarnAtPercent / 100.0, 0.1, 1.0);
        _settings.VirusTotalApiKey = (VirusTotalApiKey ?? "").Trim();
        _settings.GeoIpAutoUpdate = GeoIpAutoUpdate;

        await _client.SetSettingsAsync(_settings);
        Saved?.Invoke(_settings);
        SavedText = Loc.S("L.Set.Saved");
        await Task.Delay(2000);
        SavedText = "";
    }
}
