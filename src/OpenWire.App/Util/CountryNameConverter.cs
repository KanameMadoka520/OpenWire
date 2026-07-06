using System.Globalization;
using System.Windows.Data;

namespace OpenWire.App.Util;

/// <summary>Binds a raw/display ISO code to its localized country/region name.</summary>
public sealed class CountryNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CountryName.Localized(value as string, value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
