using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>Settings screen: monitors, data plan, resolution and firewall options.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly EngineClient _client;
    private AppSettings _settings = new();

    [ObservableProperty] private bool _resolveHostNames = true;
    [ObservableProperty] private bool _resolveGeoIp = true;
    [ObservableProperty] private bool _monitorNewDevices = true;
    [ObservableProperty] private bool _monitorDnsChanges = true;
    [ObservableProperty] private bool _monitorRdp = true;
    [ObservableProperty] private bool _monitorUsageAnomalies = true;
    [ObservableProperty] private bool _monitorHostsFile = true;
    [ObservableProperty] private bool _monitorArpSpoofing = true;
    [ObservableProperty] private int _historyRetentionDays = 90;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _dataPlanEnabled;
    [ObservableProperty] private double _dataLimitGb = 100;
    [ObservableProperty] private int _billingDay = 1;
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
        HistoryRetentionDays = s.HistoryRetentionDays;
        ShowNotifications = s.ShowDesktopNotifications;
        DataPlanEnabled = s.DataPlan.Enabled;
        DataLimitGb = s.DataPlan.LimitBytes > 0 ? s.DataPlan.LimitBytes / (1024.0 * 1024 * 1024) : 100;
        BillingDay = s.DataPlan.BillingCycleStartDay;
        VirusTotalApiKey = s.VirusTotalApiKey;

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
        _settings.HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 0, 3650);
        _settings.ShowDesktopNotifications = ShowNotifications;
        _settings.DataPlan.Enabled = DataPlanEnabled;
        _settings.DataPlan.LimitBytes = (long)(DataLimitGb * 1024 * 1024 * 1024);
        _settings.DataPlan.BillingCycleStartDay = Math.Clamp(BillingDay, 1, 28);
        _settings.VirusTotalApiKey = (VirusTotalApiKey ?? "").Trim();

        await _client.SetSettingsAsync(_settings);
        Saved?.Invoke(_settings);
        SavedText = Loc.S("L.Set.Saved");
        await Task.Delay(2000);
        SavedText = "";
    }
}
