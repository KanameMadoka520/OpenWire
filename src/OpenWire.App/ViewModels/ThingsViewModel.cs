using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

/// <summary>Things screen: the LAN device inventory with scan + manage actions.</summary>
public partial class ThingsViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private ObservableCollection<Device> _devices = new();
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _scanLabel = "Scan";
    [ObservableProperty] private string _networkName = "Local network";
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private bool _autoScan;

    public ThingsViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var resp = await _client.GetDevicesAsync();
        Fill(resp.Devices);
        if (resp.Devices.Count > 0) LastScanText = "Scanned just now";
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (Scanning) return;
        Scanning = true;
        ScanLabel = "Scanning…";
        try
        {
            // Returns immediately with the current list; the background scan then
            // streams freshly-discovered devices in via DeviceChanged events.
            var resp = await _client.GetDevicesAsync(rescan: true);
            Fill(resp.Devices);
            LastScanText = "Scanned just now";
            await Task.Delay(6000);
        }
        catch
        {
            // engine busy or offline — ignore
        }
        finally
        {
            Scanning = false;
            ScanLabel = "Scan";
        }
    }

    [RelayCommand]
    private async Task Forget(Device device)
    {
        if (device is null) return;
        await _client.ForgetDeviceAsync(device.Id);
        Devices.Remove(device);
    }

    public void OnDeviceChanged(Device device)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Id == device.Id) { Devices[i] = device; return; }
        }
        Devices.Add(device);
    }

    private void Fill(IEnumerable<Device> list) => Devices = new ObservableCollection<Device>(list);
}
