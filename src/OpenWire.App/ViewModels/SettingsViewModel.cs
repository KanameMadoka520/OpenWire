using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;

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

    public SettingsViewModel(EngineClient client) => _client = client;

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
        EngineInfo = $"Engine {hello.EngineVersion} · {hello.MachineName} · " +
                     $"firewall {(hello.CanEnforceFirewall ? "enabled" : "unavailable")} · " +
                     $"GeoIP {(hello.GeoIpAvailable ? "loaded" : "not installed")}";
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
        SavedText = "Saved ✓";
        await Task.Delay(2000);
        SavedText = "";
    }
}
