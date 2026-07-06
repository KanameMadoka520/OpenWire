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
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        // The same selector is bound to both enum properties and plain string filters
        // (e.g. the Alerts severity filter). Only parse when the target is actually an enum.
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return t.IsEnum ? Enum.Parse(t, parameter.ToString()!) : parameter.ToString()!;
    }
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

/// <summary>Hides the Usage column whose dimension index (ConverterParameter, 0 apps · 1 hosts ·
/// 2 types · 3 countries) matches the filter side-panel's active dimension: the panel already
/// lists that dimension, so showing the same list twice is redundant (GlassWire-style). The
/// bound value is -1 when the panel is closed, keeping every column visible.</summary>
public sealed class ColumnHideConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        int dim = value is int i ? i : -1;
        int col = int.TryParse(p?.ToString(), out var n) ? n : -2;
        return dim == col ? Visibility.Collapsed : Visibility.Visible;
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

/// <summary>Maps a usage-anomaly kind to a Segoe Fluent / MDL2 icon glyph.</summary>
public sealed class AnomalyKindGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        int code = value switch
        {
            AnomalyKind.VolumeSpike => 0xE9D9, // line chart / spike
            AnomalyKind.UploadHeavy => 0xE74A, // up arrow (upload)
            AnomalyKind.NewCountry => 0xE909,  // globe
            AnomalyKind.OddHour => 0xE823,     // clock / recent
            _ => 0xE7BA,                       // warning
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

/// <summary>Display label for a Usage "Countries &amp; regions" row. Uses the localized country
/// name, but when the row carries no resolvable country it falls back to the same labels the
/// filter panel uses — "Local network" for LAN traffic, "Unknown" (未知) otherwise — so the
/// column never shows a blank and reads consistently with the filter list. Bind the whole
/// <see cref="CountryUsage"/> item (it needs IsLocal, not just the code).</summary>
public sealed class CountryDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not CountryUsage cu) return "";
        if (cu.IsLocal) return Loc.S("L.Filter.LocalNetwork");
        if (string.IsNullOrEmpty(cu.CountryCode)) return Loc.S("L.Filter.Unknown");
        return CountryName.Localized(cu.CountryCode, cu.CountryName);
    }
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
        if (value is not DateTimeOffset dto) return Loc.S("L.Alerts.GroupEarlier");
        var day = dto.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        if (day == today) return Loc.S("L.Alerts.GroupToday");
        if (day == today.AddDays(-1)) return Loc.S("L.Alerts.GroupYesterday");
        return day.ToString(Loc.S("L.Alerts.GroupDateFormat"));
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
