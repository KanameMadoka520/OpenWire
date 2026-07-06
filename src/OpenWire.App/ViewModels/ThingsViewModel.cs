using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

/// <summary>Things screen: the LAN device inventory with scan + manage actions.</summary>
public partial class ThingsViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private ObservableCollection<Device> _devices = new();
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _scanLabel = Loc.S("L.Scan.Scan");
    [ObservableProperty] private string _networkName = Loc.S("L.Scan.LocalNetwork");
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasDevices;
    private readonly List<Device> _all = new();

    public ThingsViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var resp = await _client.GetDevicesAsync();
        Fill(resp.Devices);
        if (resp.Devices.Count > 0) LastScanText = Loc.S("L.Scan.ScannedJustNow");
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (Scanning) return;
        Scanning = true;
        ScanLabel = Loc.S("L.Scan.Scanning");
        try
        {
            // Returns immediately with the current list; the background scan then
            // streams freshly-discovered devices in via DeviceChanged events.
            var resp = await _client.GetDevicesAsync(rescan: true);
            Fill(resp.Devices);
            LastScanText = Loc.S("L.Scan.ScannedJustNow");
            await Task.Delay(6000);
        }
        catch
        {
            // engine busy or offline — ignore
        }
        finally
        {
            Scanning = false;
            ScanLabel = Loc.S("L.Scan.Scan");
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
        int idx = _all.FindIndex(d => d.Id == device.Id);
        if (idx >= 0) _all[idx] = device; else _all.Add(device);
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void Fill(IEnumerable<Device> list)
    {
        _all.Clear();
        _all.AddRange(list);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Device> q = _all;
        var s = SearchText?.Trim();
        if (!string.IsNullOrEmpty(s))
            q = q.Where(d => (d.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (d.IpAddress?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (d.MacAddress?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (d.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        Devices = new ObservableCollection<Device>(q);
        HasDevices = _all.Count > 0;
    }
}
