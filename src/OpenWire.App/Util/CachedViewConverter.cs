using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenWire.App.ViewModels;
using OpenWire.App.Views;

namespace OpenWire.App.Util;

/// <summary>
/// Maps a section view-model to a view instance that is created ONCE and reused
/// on every subsequent tab switch. A plain ContentControl + DataTemplate rebuilds
/// the whole visual tree on each switch — with list-heavy screens that read as a
/// noticeable hitch; reusing the tree makes switching instant (and keeps scroll
/// positions). Declare the converter in the Window's resources (not App-wide) so
/// a theme/language reskin builds a fresh window with a fresh cache and the views
/// re-resolve their StaticResources against the new dictionaries.
/// </summary>
public sealed class CachedViewConverter : IValueConverter
{
    private readonly Dictionary<object, FrameworkElement> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        if (_cache.TryGetValue(value, out var cached)) return cached;

        FrameworkElement view = value switch
        {
            TrafficViewModel => new TrafficView(),
            AnalyticsViewModel => new AnalyticsView(),
            FirewallViewModel => new FirewallView(),
            AlertsViewModel => new AlertsView(),
            ThingsViewModel => new ThingsView(),
            HardwareViewModel => new HardwareView(),
            SettingsViewModel => new SettingsView(),
            _ => new ContentControl { Content = value },
        };
        view.DataContext = value;
        _cache[value] = view;
        return view;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
