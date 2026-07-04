using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Util;

/// <summary>Two-way enum &lt;-&gt; bool match for radio-style selectors.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is long l ? ByteFormatter.Bytes(l) : value is double d ? ByteFormatter.Bytes((long)d) : "0 B";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class RateConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is double d ? ByteFormatter.Rate(d) : value is long l ? ByteFormatter.Rate(l) : "0 B/s";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is true;
        if (p is "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        long n = value switch { int i => i, long l => l, _ => 0 };
        bool visible = n > 0;
        if (p is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class FirewallStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        AppFirewallStatus.Blocked => Res("FlameBlocked"),
        AppFirewallStatus.Pending => Res("Warning"),
        _ => Res("FlameIdle"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
    private static Brush Res(string k) => (Brush)Application.Current.Resources[k];
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        AlertSeverity.Critical => Res("Danger"),
        AlertSeverity.Warning => Res("Warning"),
        _ => Res("Accent"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
    private static Brush Res(string k) => (Brush)Application.Current.Resources[k];
}

/// <summary>Colours the VirusTotal reputation cell: red when flagged, green when
/// clean, muted otherwise.</summary>
public sealed class ReputationBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        ReputationState.Flagged => Res("Danger"),
        ReputationState.Clean => Res("Success"),
        _ => Res("TextMuted"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
    private static Brush Res(string k) => (Brush)Application.Current.Resources[k];
}

/// <summary>Maps a device kind to a Segoe Fluent / MDL2 icon glyph (by code point).</summary>
public sealed class DeviceKindGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        int code = value switch
        {
            DeviceKind.ThisComputer => 0xE977, // devices / this PC
            DeviceKind.Router => 0xEC05,       // network tower
            DeviceKind.Computer => 0xE977,
            DeviceKind.Phone => 0xE8EA,        // cellphone
            DeviceKind.Tablet => 0xE70A,
            DeviceKind.Television => 0xE7F4,
            DeviceKind.GameConsole => 0xE7FC,  // game
            DeviceKind.Printer => 0xE749,      // print
            DeviceKind.Camera => 0xE722,       // camera
            DeviceKind.SmartHome => 0xE80F,    // home
            DeviceKind.Server => 0xE968,
            _ => 0xE977,
        };
        return ((char)code).ToString();
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Visible when the bound enum equals the parameter, else Collapsed.</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Multiplies a 0..1 fraction by the numeric parameter to get a bar width.</summary>
public sealed class FractionWidthConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        double f = value is double d ? d : 0;
        double max = p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 100;
        return Math.Max(0, Math.Min(1, f)) * max;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is DateTimeOffset dto ? dto.ToLocalTime().ToString(p as string ?? "MMM d  HH:mm") : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Buckets a timestamp into Today / Yesterday / Earlier for alert grouping.</summary>
public sealed class DayGroupConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not DateTimeOffset dto) return "Earlier";
        var day = dto.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString("MMMM d");
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
