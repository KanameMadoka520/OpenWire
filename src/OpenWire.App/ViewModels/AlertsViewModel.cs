using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Models;

namespace OpenWire.App.ViewModels;

/// <summary>Alerts screen: chronological, filterable, searchable event log.</summary>
public partial class AlertsViewModel : ObservableObject
{
    private readonly EngineClient _client;
    private List<Alert> _all = new();

    [ObservableProperty] private ObservableCollection<Alert> _alerts = new();
    [ObservableProperty] private string _severityFilter = "All";   // All / Important / Log
    [ObservableProperty] private string _timeFilter = "All";       // All / Today / Week
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _importantCount;
    [ObservableProperty] private int _logCount;
    [ObservableProperty] private string _emptyText = Loc.S("L.Alerts.EmptyNone");

    public AlertsViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var resp = await _client.GetAlertsAsync(500);
        _all = resp.Alerts;
        UpdateCounts();
        ApplyFilter();
    }

    public void OnAlertRaised(Alert alert)
    {
        _all.Insert(0, alert);
        UpdateCounts();
        ApplyFilter();
    }

    private void UpdateCounts()
    {
        AllCount = _all.Count;
        ImportantCount = _all.Count(a => a.Severity != AlertSeverity.Info);
        LogCount = _all.Count(a => a.Severity == AlertSeverity.Info);
    }

    private void ApplyFilter()
    {
        IEnumerable<Alert> q = _all;
        if (SeverityFilter == "Important") q = q.Where(a => a.Severity != AlertSeverity.Info);
        else if (SeverityFilter == "Log") q = q.Where(a => a.Severity == AlertSeverity.Info);

        var now = DateTimeOffset.Now;
        if (TimeFilter == "Today") q = q.Where(a => a.Time.ToLocalTime().Date == now.Date);
        else if (TimeFilter == "Week") q = q.Where(a => a.Time.ToLocalTime() >= now.AddDays(-7));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            q = q.Where(a => (a.Title?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                          || (a.Message?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Alerts = new ObservableCollection<Alert>(q);

        bool filtered = SeverityFilter != "All" || !string.IsNullOrWhiteSpace(SearchText);
        EmptyText = _all.Count > 0 && Alerts.Count == 0 && filtered
            ? Loc.S("L.Alerts.EmptyFiltered")
            : Loc.S("L.Alerts.EmptyNone");
    }

    partial void OnSeverityFilterChanged(string value) => ApplyFilter();
    partial void OnTimeFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task MarkAllRead()
    {
        await _client.AckAlertAsync(0, all: true);
        await LoadAsync();
    }
}
