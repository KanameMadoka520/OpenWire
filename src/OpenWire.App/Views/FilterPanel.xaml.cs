using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenWire.App.Util;
using OpenWire.App.ViewModels;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

/// <summary>
/// GlassWire-style filter side panel for the Traffic screen: one ranked
/// [icon] name [in] [out] list, switchable between Applications / Hosts /
/// Traffic types / Countries, with WAN-LAN, direction and name-search filters.
/// Self-contained: point its DataContext at the shared <see cref="UsageViewModel"/>
/// and it re-applies the filters whenever the usage collections reload.
/// </summary>
public partial class FilterPanel : UserControl
{
    /// <summary>Raised when the user clicks the panel's close (X) button.</summary>
    public event Action? CloseRequested;

    /// <summary>Opacity of the down (in) column; lowered when sorting by outbound only.</summary>
    public static readonly DependencyProperty DownColumnOpacityProperty = DependencyProperty.Register(
        nameof(DownColumnOpacity), typeof(double), typeof(FilterPanel), new PropertyMetadata(1.0));

    /// <summary>Opacity of the up (out) column; lowered when sorting by inbound only.</summary>
    public static readonly DependencyProperty UpColumnOpacityProperty = DependencyProperty.Register(
        nameof(UpColumnOpacity), typeof(double), typeof(FilterPanel), new PropertyMetadata(1.0));

    public double DownColumnOpacity
    {
        get => (double)GetValue(DownColumnOpacityProperty);
        set => SetValue(DownColumnOpacityProperty, value);
    }

    public double UpColumnOpacity
    {
        get => (double)GetValue(UpColumnOpacityProperty);
        set => SetValue(UpColumnOpacityProperty, value);
    }

    private UsageViewModel? _vm;

    public FilterPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---- view-model wiring ----

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Attach(e.NewValue as UsageViewModel);
        Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach(DataContext as UsageViewModel); // re-attach after an Unloaded detach
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Attach(null);

    private void Attach(UsageViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The VM replaces the collections wholesale on every load.
        if (e.PropertyName is nameof(UsageViewModel.Apps) or nameof(UsageViewModel.Hosts)
            or nameof(UsageViewModel.Types) or nameof(UsageViewModel.Countries))
            Refresh();
    }

    // ---- header controls ----

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int dir = ((ComboBox)sender).SelectedIndex; // 0 in+out · 1 in · 2 out
        DownColumnOpacity = dir == 2 ? 0.35 : 1.0;
        UpColumnOpacity = dir == 1 ? 0.35 : 1.0;
        Refresh();
    }

    private void OnSearchToggled(object sender, RoutedEventArgs e)
    {
        if (SearchOverlay is null) return;
        bool on = SearchToggle.IsChecked == true;
        SearchOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) SearchBox.Focus();
        else if (SearchBox.Text.Length > 0) SearchBox.Clear(); // TextChanged re-applies the filter
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchToggle.IsChecked = false; // Unchecked → OnSearchToggled
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ---- column sort (click a header to sort by it) ----
    // "" = follow the direction dropdown (default); otherwise name / in / out.
    private string _sortKey = "";
    private bool _sortAsc;

    private void OnSortName(object sender, RoutedEventArgs e) => ToggleSort("name", defaultAsc: true);
    private void OnSortIn(object sender, RoutedEventArgs e) => ToggleSort("in", defaultAsc: false);
    private void OnSortOut(object sender, RoutedEventArgs e) => ToggleSort("out", defaultAsc: false);

    private void ToggleSort(string key, bool defaultAsc)
    {
        if (_sortKey == key) _sortAsc = !_sortAsc;
        else { _sortKey = key; _sortAsc = defaultAsc; }
        Refresh();
    }

    private void UpdateSortArrows()
    {
        if (NameArrow is null) return;
        string Arrow(string k) => _sortKey == k ? (_sortAsc ? " ▲" : " ▼") : "";
        NameArrow.Text = Arrow("name");
        InArrow.Text = Arrow("in") == "" ? "" : (_sortAsc ? "▲ " : "▼ ");
        OutArrow.Text = Arrow("out") == "" ? "" : (_sortAsc ? "▲ " : "▼ ");
    }

    // ---- list building ----

    /// <summary>Rebuilds the visible list from the active dimension + filters.</summary>
    private void Refresh()
    {
        if (List is null) return; // selection events fired during InitializeComponent

        int dim = DimCombo.SelectedIndex;    // 0 apps · 1 hosts · 2 types · 3 countries
        int wanLan = WanCombo.SelectedIndex; // 0 both · 1 LAN · 2 WAN
        int dir = DirCombo.SelectedIndex;    // 0 in+out · 1 in · 2 out
        string search = SearchBox.Text.Trim();

        IEnumerable<Row> rows = dim switch
        {
            1 => (_vm?.Hosts ?? Enumerable.Empty<HostUsage>())
                .Where(h => MatchesWanLan(wanLan, h.Geo.IsLocal || IsPrivateAddress(h.RemoteAddress)))
                .Select(h => new Row
                {
                    Name = h.Host, FlagCode = h.Geo.CountryCode, HasFlagSlot = true,
                    BytesIn = h.BytesIn, BytesOut = h.BytesOut,
                }),
            2 => (_vm?.Types ?? Enumerable.Empty<TrafficTypeUsage>())
                .Select(t => new Row
                {
                    Name = t.TypeName, Glyph = "\uE9D9",
                    BytesIn = t.BytesIn, BytesOut = t.BytesOut,
                }),
            3 => (_vm?.Countries ?? Enumerable.Empty<CountryUsage>())
                .Where(c => MatchesWanLan(wanLan, c.IsLocal || string.IsNullOrEmpty(c.CountryCode)))
                .Select(c => new Row
                {
                    Name = string.IsNullOrEmpty(c.DisplayName)
                        ? Loc.S(c.IsLocal ? "L.Filter.LocalNetwork" : "L.Filter.Unknown")
                        : c.DisplayName,
                    FlagCode = c.CountryCode, HasFlagSlot = true, CodeBadge = c.DisplayCode,
                    BytesIn = c.BytesIn, BytesOut = c.BytesOut,
                }),
            _ => (_vm?.Apps ?? Enumerable.Empty<AppUsage>())
                .Select(a => new Row
                {
                    Name = a.App.Name, IconPath = a.App.ExecutablePath, IsApp = true,
                    BytesIn = a.BytesIn, BytesOut = a.BytesOut,
                }),
        };

        if (search.Length > 0)
            rows = rows.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        // A clicked column header overrides the direction dropdown's default sort.
        IEnumerable<Row> sorted = _sortKey switch
        {
            "name" => _sortAsc ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                               : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "in" => _sortAsc ? rows.OrderBy(r => r.BytesIn) : rows.OrderByDescending(r => r.BytesIn),
            "out" => _sortAsc ? rows.OrderBy(r => r.BytesOut) : rows.OrderByDescending(r => r.BytesOut),
            _ => dir switch
            {
                1 => rows.OrderByDescending(r => r.BytesIn),
                2 => rows.OrderByDescending(r => r.BytesOut),
                _ => rows.OrderByDescending(r => r.BytesIn + r.BytesOut),
            },
        };
        var list = sorted.ToList();
        UpdateSortArrows();

        List.ItemsSource = list;
        ListLabel.Text = Loc.S(dim switch
        {
            1 => "L.Filter.HeaderHosts",
            2 => "L.Filter.HeaderTrafficTypes",
            3 => "L.Filter.HeaderCountries",
            _ => "L.Filter.HeaderApplications",
        });
        EmptyNote.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Apps and traffic types carry no per-item WAN/LAN split — say so instead of guessing.
        SplitNote.Visibility = wanLan != 0 && dim is 0 or 2 ? Visibility.Visible : Visibility.Collapsed;
        OneChinaInfo.Visibility = dim == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Applies the WAN/LAN dropdown to an entry's locality flag.</summary>
    private static bool MatchesWanLan(int wanLan, bool isLan)
        => wanLan == 0 || (wanLan == 1) == isLan;

    /// <summary>
    /// True for private / local remote addresses — RFC1918 (10/8, 172.16/12,
    /// 192.168/16), loopback and 169.254/16 link-local IPv4; loopback, fe80::/10
    /// link-local and fc00::/7 unique-local IPv6. Unparseable input counts as public.
    /// </summary>
    private static bool IsPrivateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || b[0] == 127
                || (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }

    /// <summary>One display row of the filter list, shared by all four dimensions.</summary>
    public sealed class Row
    {
        public string Name { get; init; } = "";

        /// <summary>Executable path fed to the AppIcon converter (Applications rows).</summary>
        public string IconPath { get; init; } = "";

        /// <summary>ISO country code fed to the Flag converter (Hosts / Countries rows).</summary>
        public string FlagCode { get; init; } = "";

        /// <summary>Segoe Fluent glyph shown instead of an icon (Traffic-type rows).</summary>
        public string Glyph { get; init; } = "";

        /// <summary>One-China display-code badge (Countries rows).</summary>
        public string CodeBadge { get; init; } = "";

        /// <summary>True on Applications rows: keeps the 16 px icon slot even for
        /// icon-less binaries so names stay aligned.</summary>
        public bool IsApp { get; init; }

        /// <summary>True on Hosts / Countries rows: reserves the flag slot.</summary>
        public bool HasFlagSlot { get; init; }

        public long BytesIn { get; init; }
        public long BytesOut { get; init; }
    }
}
