using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

public enum Section { Graph, Firewall, Usage, Alerts, Things, Settings }

/// <summary>Root view-model: owns the shell state, the headline status, the six
/// screen view-models, and the wiring of engine events into the UI.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private Section _currentSection = Section.Graph;
    [ObservableProperty] private ObservableObject? _currentView;

    [ObservableProperty] private string _machineName = Environment.MachineName;
    [ObservableProperty] private string _engineVersion = "";
    [ObservableProperty] private string _downRate = "0 B/s";
    [ObservableProperty] private string _upRate = "0 B/s";
    [ObservableProperty] private string _totalDown = "0 B";
    [ObservableProperty] private string _totalUp = "0 B";
    [ObservableProperty] private int _unreadAlerts;
    [ObservableProperty] private int _onlineDevices;
    [ObservableProperty] private string _firewallModeText = "Off";
    [ObservableProperty] private bool _canEnforceFirewall;

    public GraphViewModel Graph { get; }
    public FirewallViewModel Firewall { get; }
    public UsageViewModel Usage { get; }
    public AlertsViewModel Alerts { get; }
    public ThingsViewModel Things { get; }
    public SettingsViewModel Settings { get; }

    public EngineClient Client => _client;

    public MainViewModel(EngineClient client)
    {
        _client = client;
        Graph = new GraphViewModel(client);
        Firewall = new FirewallViewModel(client);
        Usage = new UsageViewModel(client);
        Alerts = new AlertsViewModel(client);
        Things = new ThingsViewModel(client);
        Settings = new SettingsViewModel(client);
        CurrentView = Graph;

        client.ConnectionChanged += OnConnectionChanged;
        client.LiveTick += OnLiveTick;
        client.StatusChanged += e => ApplyStatus(e.Status);
        client.AlertRaised += e => { UnreadAlerts++; Alerts.OnAlertRaised(e.Alert); };
        client.DeviceChanged += e => Things.OnDeviceChanged(e.Device);
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
            var status = await _client.GetStatusAsync();
            ApplyStatus(status.Status);
            await ActivateSectionAsync(CurrentSection);
        }
        catch { /* transient during (re)connect */ }
    }

    private void OnLiveTick(Core.Ipc.LiveTickEvent e)
    {
        DownRate = ByteFormatter.Rate(e.DownloadBytesPerSec);
        UpRate = ByteFormatter.Rate(e.UploadBytesPerSec);
        Graph.PushTick(e.Sample.Time, e.DownloadBytesPerSec, e.UploadBytesPerSec);
    }

    private void ApplyStatus(EngineStatus s)
    {
        MachineName = s.MachineName;
        TotalDown = ByteFormatter.Bytes(s.TotalBytesIn);
        TotalUp = ByteFormatter.Bytes(s.TotalBytesOut);
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
            Section.Graph => Graph,
            Section.Firewall => Firewall,
            Section.Usage => Usage,
            Section.Alerts => Alerts,
            Section.Things => Things,
            Section.Settings => Settings,
            _ => Graph,
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
                case Section.Graph: await Graph.LoadAsync(); break;
                case Section.Firewall: await Firewall.LoadAsync(); break;
                case Section.Usage: await Usage.LoadAsync(); break;
                case Section.Alerts: await Alerts.LoadAsync(); break;
                case Section.Things: await Things.LoadAsync(); break;
                case Section.Settings: await Settings.LoadAsync(); break;
            }
        }
        catch { /* ignore transient load errors */ }
    }

    [RelayCommand]
    private void ShowAlerts() => CurrentSection = Section.Alerts;

    [RelayCommand]
    private void StartEngine()
    {
        try
        {
            string? exe = LocateServiceExe();
            if (exe is null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas", // triggers UAC; the service needs elevation
            });
        }
        catch { /* user declined UAC */ }
    }

    private static string? LocateServiceExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "OpenWire.Service.exe");
        if (File.Exists(local)) return local;

        // Dev layout: sibling project bin folder.
        string config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "OpenWire.Service", "bin", config, "net9.0-windows", "OpenWire.Service.exe"));
        return File.Exists(dev) ? dev : null;
    }
}
