using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.ViewModels;

/// <summary>Per-column sort state for a single-metric Usage list: which metric
/// ("total" or "name") and the direction. Observable so the header arrow updates
/// live. Toggling cycles a column through total↓ → name↑ → name↓ → total↑ → total↓,
/// so every ordering is reachable and the arrow always reflects the direction.</summary>
public partial class ColumnSort : ObservableObject
{
    [ObservableProperty] private string _key;
    [ObservableProperty] private bool _ascending;

    public ColumnSort(string key, bool ascending)
    {
        _key = key;
        _ascending = ascending;
    }

    public void Toggle()
    {
        if (Key == "total")
        {
            if (!Ascending) { Key = "name"; Ascending = true; } // total↓ (default) → name↑ (A→Z)
            else Ascending = false;                             // total↑ → total↓ (back to default)
        }
        else // name
        {
            if (Ascending) Ascending = false;                   // name↑ → name↓ (reverse)
            else { Key = "total"; Ascending = true; }           // name↓ → total↑
        }
    }
}

/// <summary>Usage screen: byte totals grouped by apps / hosts / traffic type over
/// a chosen window.</summary>
public partial class UsageViewModel : ObservableObject
{
    private readonly EngineClient _client;

    [ObservableProperty] private UsageGroupBy _groupBy = UsageGroupBy.Apps;
    [ObservableProperty] private GraphRange _range = GraphRange.Day;
    [ObservableProperty] private ObservableCollection<AppUsage> _apps = new();
    [ObservableProperty] private ObservableCollection<HostUsage> _hosts = new();
    [ObservableProperty] private ObservableCollection<TrafficTypeUsage> _types = new();
    [ObservableProperty] private ObservableCollection<CountryUsage> _countries = new();
    [ObservableProperty] private string _totalText = "";

    /// <summary>Dimension index shown in the filter side-panel (0 apps · 1 hosts · 2 types ·
    /// 3 countries), or -1 when the panel is closed. The matching Usage column hides itself so
    /// the same list isn't presented twice — the filter panel becomes that column (GlassWire-style).</summary>
    [ObservableProperty] private int _filterDimension = -1;

    // Kept sort state per column (default: total, high→low — matches the engine order).
    // Held state so re-sorting happens on header click and survives range reloads,
    // rather than re-sorting on every live tick.
    public ColumnSort AppsSort { get; } = new("total", false);
    public ColumnSort HostsSort { get; } = new("total", false);
    public ColumnSort TypesSort { get; } = new("total", false);
    public ColumnSort CountriesSort { get; } = new("total", false);

    // Unsorted source rows for each column, so a header click re-sorts without a re-fetch.
    private List<AppUsage> _appsRaw = new();
    private List<HostUsage> _hostsRaw = new();
    private List<TrafficTypeUsage> _typesRaw = new();
    private List<CountryUsage> _countriesRaw = new();

    public UsageViewModel(EngineClient client) => _client = client;

    public async Task LoadAsync()
    {
        var u = await _client.GetUsageAsync(Range, GroupBy);
        _appsRaw = u.Apps.Take(60).ToList();
        _hostsRaw = u.Hosts.Take(60).ToList();
        _typesRaw = u.Types.ToList();
        _countriesRaw = u.Countries.Take(40).ToList();

        // Re-apply the kept sort state so reloads don't reset the user's column choices.
        ApplyAppsSort();
        ApplyHostsSort();
        ApplyTypesSort();
        ApplyCountriesSort();

        TotalText = $"{ByteFormatter.Bytes(u.TotalBytesIn + u.TotalBytesOut)} · " +
                    $"down {ByteFormatter.Bytes(u.TotalBytesIn)} · up {ByteFormatter.Bytes(u.TotalBytesOut)}";
    }

    /// <summary>Toggle a column's sort (name ↔ total, reversing on repeat clicks)
    /// and rebuild only that column's collection. Sort state is kept so it survives
    /// range reloads.</summary>
    [RelayCommand]
    private void SortColumn(string column)
    {
        switch (column)
        {
            case "apps": AppsSort.Toggle(); ApplyAppsSort(); break;
            case "hosts": HostsSort.Toggle(); ApplyHostsSort(); break;
            case "types": TypesSort.Toggle(); ApplyTypesSort(); break;
            case "countries": CountriesSort.Toggle(); ApplyCountriesSort(); break;
        }
    }

    /// <summary>Export the current usage breakdown (all four dimensions, in their on-screen order)
    /// to a CSV file the user picks. A GlassWire-parity "export usage data" action.</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.S("L.Usage.ExportTitle"),
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"openwire-usage-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.Append("Category,Name,Code,Download (bytes),Upload (bytes),Total (bytes)\n");
        foreach (var a in Apps)      Row(sb, "Application", a.App.Name, "", a.BytesIn, a.BytesOut, a.Total);
        foreach (var h in Hosts)     Row(sb, "Host", h.Host, "", h.BytesIn, h.BytesOut, h.Total);
        foreach (var t in Types)     Row(sb, "Traffic type", t.TypeName, "", t.BytesIn, t.BytesOut, t.Total);
        foreach (var c in Countries) Row(sb, "Country", c.DisplayName, c.CountryCode, c.BytesIn, c.BytesOut, c.Total);

        try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); }
        catch { /* file locked / no permission — the picker already committed, nothing else to do */ }
    }

    private static void Row(StringBuilder sb, string category, string name, string code, long inB, long outB, long total)
        => sb.Append(category).Append(',').Append(Csv(name)).Append(',').Append(Csv(code)).Append(',')
             .Append(inB).Append(',').Append(outB).Append(',').Append(total).Append('\n');

    /// <summary>Neutralize spreadsheet formulas, then apply RFC-4180 field escaping.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? "'" + value
            : value;
        if (safe.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private void ApplyAppsSort() => Apps = Sort(_appsRaw, AppsSort, a => a.App.Name, a => a.Total);
    private void ApplyHostsSort() => Hosts = Sort(_hostsRaw, HostsSort, h => h.Host, h => h.Total);
    private void ApplyTypesSort() => Types = Sort(_typesRaw, TypesSort, t => t.TypeName, t => t.Total);
    private void ApplyCountriesSort() => Countries = Sort(_countriesRaw, CountriesSort, c => c.DisplayName, c => c.Total);

    private static ObservableCollection<T> Sort<T>(IEnumerable<T> src, ColumnSort s,
        Func<T, string> name, Func<T, long> total)
    {
        IEnumerable<T> q = s.Key == "name"
            ? (s.Ascending ? src.OrderBy(name, StringComparer.OrdinalIgnoreCase)
                           : src.OrderByDescending(name, StringComparer.OrdinalIgnoreCase))
            : (s.Ascending ? src.OrderBy(total)
                           : src.OrderByDescending(total));
        return new ObservableCollection<T>(q);
    }

    partial void OnGroupByChanged(UsageGroupBy value) => _ = LoadAsync();
    partial void OnRangeChanged(GraphRange value) => _ = LoadAsync();
}
