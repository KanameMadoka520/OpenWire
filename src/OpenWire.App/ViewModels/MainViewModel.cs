using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

public enum Section { Traffic, Firewall, Alerts, Scanner, Hardware, Settings }

/// <summary>Root view-model: shell state, headline dashboard status, and the
/// screen view-models. Mirrors GlassWire's five top tabs (Traffic / Firewall /
/// Alerts / Scanner / Hardware) plus a Settings pane.</summary>
public partial class MainViewModel : ObservableObject
{
    private const double BarMax = 320; // px reference width for the WAN/LAN bars

    private readonly EngineClient _client;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private Section _currentSection = Section.Traffic;
    [ObservableProperty] private ObservableObject? _currentView;

    [ObservableProperty] private string _machineName = Environment.MachineName;
    [ObservableProperty] private string _engineVersion = "";
    [ObservableProperty] private string _downRate = "0 B/s";
    [ObservableProperty] private string _upRate = "0 B/s";

    // Bottom dashboard
    [ObservableProperty] private string _totalDown = "0 B";
    [ObservableProperty] private string _totalUp = "0 B";
    [ObservableProperty] private string _totalCombined = "0 B";
    [ObservableProperty] private double _downValue;
    [ObservableProperty] private double _upValue;
    [ObservableProperty] private string _wanText = "0 B";
    [ObservableProperty] private string _lanText = "0 B";
    [ObservableProperty] private double _wanBarWidth;
    [ObservableProperty] private double _lanBarWidth;

    [ObservableProperty] private int _unreadAlerts;
    [ObservableProperty] private int _onlineDevices;
    [ObservableProperty] private string _firewallModeText = "Off";
    [ObservableProperty] private bool _canEnforceFirewall;

    public TrafficViewModel Traffic { get; }
    public FirewallViewModel Firewall { get; }
    public AlertsViewModel Alerts { get; }
    public ThingsViewModel Scanner { get; }
    public HardwareViewModel Hardware { get; }
    public SettingsViewModel Settings { get; }

    public EngineClient Client => _client;

    public MainViewModel(EngineClient client)
    {
        _client = client;
        Traffic = new TrafficViewModel(client);
        Firewall = new FirewallViewModel(client);
        Alerts = new AlertsViewModel(client);
        Scanner = new ThingsViewModel(client);
        Hardware = new HardwareViewModel(client);
        Settings = new SettingsViewModel(client);
        CurrentView = Traffic;

        client.ConnectionChanged += OnConnectionChanged;
        client.LiveTick += OnLiveTick;
        client.StatusChanged += e => ApplyStatus(e.Status);
        client.AlertRaised += e => { UnreadAlerts++; Alerts.OnAlertRaised(e.Alert); };
        client.DeviceChanged += e => Scanner.OnDeviceChanged(e.Device);
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        if (connected) _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var hello = await _client.HelloAsync();
            EngineVersion = hello.EngineVersion;
            CanEnforceFirewall = hello.CanEnforceFirewall;
            ApplyStatus((await _client.GetStatusAsync()).Status);
            await ActivateSectionAsync(CurrentSection);
        }
        catch { /* transient during (re)connect */ }
    }

    private void OnLiveTick(Core.Ipc.LiveTickEvent e)
    {
        DownRate = ByteFormatter.Rate(e.DownloadBytesPerSec);
        UpRate = ByteFormatter.Rate(e.UploadBytesPerSec);
        Traffic.PushTick(e.Sample.Time, e.DownloadBytesPerSec, e.UploadBytesPerSec);
    }

    private void ApplyStatus(EngineStatus s)
    {
        MachineName = s.MachineName;
        TotalDown = ByteFormatter.Bytes(s.TotalBytesIn);
        TotalUp = ByteFormatter.Bytes(s.TotalBytesOut);
        TotalCombined = ByteFormatter.Bytes(s.TotalBytesIn + s.TotalBytesOut);
        DownValue = s.TotalBytesIn;
        UpValue = s.TotalBytesOut;

        WanText = ByteFormatter.Bytes(s.TotalWanBytes);
        LanText = ByteFormatter.Bytes(s.TotalLanBytes);
        double barMax = Math.Max(1, Math.Max(s.TotalWanBytes, s.TotalLanBytes));
        WanBarWidth = s.TotalWanBytes / barMax * BarMax;
        LanBarWidth = s.TotalLanBytes / barMax * BarMax;

        UnreadAlerts = s.UnreadAlertCount;
        OnlineDevices = s.OnlineDeviceCount;
        FirewallModeText = FormatMode(s.FirewallMode);
        CanEnforceFirewall = s.CanEnforceFirewall;
    }

    private static string FormatMode(FirewallMode m) => m switch
    {
        FirewallMode.Off => "Firewall off",
        FirewallMode.ClickToBlock => "Click to block",
        FirewallMode.AskToConnect => "Ask to connect",
        _ => m.ToString(),
    };

    partial void OnCurrentSectionChanged(Section value)
    {
        CurrentView = value switch
        {
            Section.Traffic => Traffic,
            Section.Firewall => Firewall,
            Section.Alerts => Alerts,
            Section.Scanner => Scanner,
            Section.Hardware => Hardware,
            Section.Settings => Settings,
            _ => Traffic,
        };
        if (IsConnected) _ = ActivateSectionAsync(value);
        if (value == Section.Alerts) UnreadAlerts = 0;
    }

    private async Task ActivateSectionAsync(Section value)
    {
        try
        {
            switch (value)
            {
                case Section.Traffic: await Traffic.LoadAsync(); break;
                case Section.Firewall: await Firewall.LoadAsync(); break;
                case Section.Alerts: await Alerts.LoadAsync(); break;
                case Section.Scanner: await Scanner.LoadAsync(); break;
                case Section.Hardware: await Hardware.LoadAsync(); break;
                case Section.Settings: await Settings.LoadAsync(); break;
            }
        }
        catch { /* ignore transient load errors */ }
    }

    [RelayCommand] private void ShowAlerts() => CurrentSection = Section.Alerts;
    [RelayCommand] private void ShowSettings() => CurrentSection = Section.Settings;

    [RelayCommand]
    private void StartEngine()
    {
        try
        {
            string? exe = LocateServiceExe();
            if (exe is null) return;
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
        }
        catch { /* user declined UAC */ }
    }

    private static string? LocateServiceExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "OpenWire.Service.exe");
        if (File.Exists(local)) return local;

        string config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "OpenWire.Service", "bin", config, "net9.0-windows", "OpenWire.Service.exe"));
        return File.Exists(dev) ? dev : null;
    }
}
