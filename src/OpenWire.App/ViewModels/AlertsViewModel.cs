using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

/// <summary>Alerts screen: chronological, filterable event log.</summary>
public partial class AlertsViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private ObservableCollection<Alert> _alerts = new();
    [ObservableProperty] private string _emptyText = "No alerts yet. OpenWire will report new apps, devices and security events here.";

    public AlertsViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var resp = await _client.GetAlertsAsync(300);
        Alerts = new ObservableCollection<Alert>(resp.Alerts);
    }

    public void OnAlertRaised(Alert alert) => Alerts.Insert(0, alert);

    [RelayCommand]
    private async Task MarkAllRead()
    {
        await _client.AckAlertAsync(0, all: true);
        await LoadAsync();
    }
}
