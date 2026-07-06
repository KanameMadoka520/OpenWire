using System.Globalization;
using System.Windows.Data;

namespace OpenWire.App.Util;

/// <summary>
/// Shows a sort direction arrow next to the active column header.
/// values[0] = the view-model's current sort key, values[1] = ascending (bool);
/// ConverterParameter = this column's key. Returns "▲"/"▼" on the active column,
/// an empty string otherwise.
/// </summary>
public sealed class SortArrowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string active || parameter is not string col) return "";
        if (!string.Equals(active, col, StringComparison.Ordinal)) return "";
        bool asc = values[1] is true;
        return asc ? " ▲" : " ▼";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
