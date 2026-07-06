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

    // Column sort: empty field = natural order (as the engine returned it). The sort
    // is applied when a header is clicked and re-applied on every rebuild, so it
    // survives rescans and live device updates — rows are never re-sorted on a bare
    // tick with no ordering key set, so they stay put until the user picks a column.
    [ObservableProperty] private string _sortField = "";
    [ObservableProperty] private bool _sortAscending;

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

    /// <summary>Sort by a column; clicking the active column again reverses it.
    /// Text columns default A→Z ascending; the two timestamp columns default to
    /// most-recent-first (descending). Called from the header click handler.</summary>
    public void SortBy(string field)
    {
        if (string.Equals(field, SortField, StringComparison.Ordinal))
            SortAscending = !SortAscending;
        else
        {
            SortField = field;
            SortAscending = field is not ("lastseen" or "firstseen");
        }
        ApplyFilter();
    }

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
        q = ApplySort(q);
        Devices = new ObservableCollection<Device>(q);
        HasDevices = _all.Count > 0;
    }

    private IEnumerable<Device> ApplySort(IEnumerable<Device> q)
    {
        switch (SortField)
        {
            case "name":        return OrderText(q, d => d.Name);
            case "description": return OrderText(q, d => d.Description);
            case "system":      return OrderText(q, d => d.OperatingSystem);
            case "mac":         return OrderText(q, d => d.MacAddress);
            case "ip":          return SortAscending ? q.OrderBy(d => IpKey(d.IpAddress))
                                                     : q.OrderByDescending(d => IpKey(d.IpAddress));
            case "lastseen":    return SortAscending ? q.OrderBy(d => d.LastSeen)
                                                     : q.OrderByDescending(d => d.LastSeen);
            case "firstseen":   return SortAscending ? q.OrderBy(d => d.FirstSeen)
                                                     : q.OrderByDescending(d => d.FirstSeen);
            default:            return q;   // "" → natural order, as the engine returned it
        }
    }

    private IEnumerable<Device> OrderText(IEnumerable<Device> q, Func<Device, string> key)
        => SortAscending ? q.OrderBy(key, StringComparer.OrdinalIgnoreCase)
                         : q.OrderByDescending(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Packs an IPv4 dotted-quad into a sortable integer so addresses order
    /// numerically (…1.2 before …1.10); anything unparseable sorts last.</summary>
    private static long IpKey(string? ip)
    {
        if (System.Net.IPAddress.TryParse(ip, out var addr)
            && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
        }
        return long.MaxValue;
    }
}
