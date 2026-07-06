using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenWire.Core.Util;

namespace OpenWire.App.Util;

/// <summary>
/// Maps an ISO 3166-1 alpha-2 country code (e.g. "US", "DE") to its bundled
/// famfamfam flag image. The flags are compiled in as WPF resources and named by
/// lower-case country code, so the lookup is a direct pack-URI load, cached per
/// code (including negative results, so a missing flag is only probed once).
/// Returns null for empty / unknown / non-existent codes so the bound
/// <c>Image</c> simply renders nothing.
/// </summary>
public sealed class CountryFlagConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string raw) return null;
        // One-China: Taiwan renders the national (CN) flag; also accept "CN-XX" display codes.
        string code = OneChina.FlagCode(raw.Trim()).ToLowerInvariant();
        if (code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            return null;
        return Cache.GetOrAdd(code, Load);
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;

    private static ImageSource? Load(string code)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Flags/{code}.png", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // no flag for this code — render nothing
        }
    }
}
