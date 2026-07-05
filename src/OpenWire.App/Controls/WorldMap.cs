using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// A world connection map: a lat/long graticule with a bubble per country that
/// exchanged traffic, positioned by an equirectangular projection and sized by its
/// share. Replaces GlassWire's globe view (data-limited without a GeoIP database,
/// but a real, live map rather than a placeholder). Reads colours from the active skin.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private IReadOnlyList<CountryUsage> _data = Array.Empty<CountryUsage>();
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    // Approximate country centroids (latitude, longitude).
    private static readonly Dictionary<string, (double Lat, double Lon)> Geo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = (38, -97), ["CA"] = (56, -106), ["MX"] = (23, -102), ["BR"] = (-10, -55), ["AR"] = (-38, -63),
        ["CL"] = (-33, -71), ["GB"] = (54, -2), ["IE"] = (53, -8), ["FR"] = (46, 2), ["DE"] = (51, 10),
        ["NL"] = (52, 5), ["BE"] = (50, 5), ["ES"] = (40, -4), ["PT"] = (39, -8), ["IT"] = (43, 12),
        ["CH"] = (47, 8), ["AT"] = (47, 14), ["SE"] = (62, 15), ["NO"] = (62, 10), ["FI"] = (64, 26),
        ["DK"] = (56, 10), ["PL"] = (52, 20), ["CZ"] = (49, 16), ["RO"] = (46, 25), ["RU"] = (60, 100),
        ["UA"] = (49, 32), ["TR"] = (39, 35), ["CN"] = (35, 105), ["HK"] = (22, 114), ["TW"] = (24, 121),
        ["JP"] = (36, 138), ["KR"] = (36, 128), ["IN"] = (21, 78), ["SG"] = (1, 104), ["MY"] = (4, 102),
        ["ID"] = (-2, 118), ["TH"] = (15, 101), ["VN"] = (16, 108), ["PH"] = (13, 122), ["AU"] = (-25, 133),
        ["NZ"] = (-42, 172), ["ZA"] = (-29, 24), ["AE"] = (24, 54), ["SA"] = (24, 45), ["IL"] = (31, 35),
        ["EG"] = (27, 30), ["NG"] = (9, 8), ["KE"] = (0, 38),
    };

    public void SetData(IReadOnlyList<CountryUsage> data) { _data = data ?? Array.Empty<CountryUsage>(); InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo s) { base.OnRenderSizeChanged(s); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;

        var grid = new Pen(new SolidColorBrush(Res("GridLineColor", Color.FromRgb(0xE4, 0xE7, 0xEB))), 1);
        var frame = new Pen(new SolidColorBrush(Res("BorderStrongColor", Color.FromRgb(0x8B, 0x92, 0x9B))), 1.2);
        var text = new SolidColorBrush(Res("TextMutedColor", Color.FromRgb(0x79, 0x81, 0x8B)));
        Color accent = Res("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));
        var bubble = new SolidColorBrush(Color.FromArgb(0x5E, accent.R, accent.G, accent.B));
        var bubbleLine = new Pen(new SolidColorBrush(accent), 1.2);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double pad = 14, mw = w - 2 * pad, mh = h - 2 * pad;
        double X(double lon) => pad + (lon + 180) / 360 * mw;
        double Y(double lat) => pad + (90 - lat) / 180 * mh;

        dc.DrawRectangle(null, frame, new Rect(pad, pad, mw, mh));
        for (int lon = -150; lon <= 150; lon += 30) dc.DrawLine(grid, new Point(X(lon), pad), new Point(X(lon), pad + mh));
        for (int lat = -60; lat <= 60; lat += 30) dc.DrawLine(grid, new Point(pad, Y(lat)), new Point(pad + mw, Y(lat)));
        // equator, a touch stronger
        dc.DrawLine(new Pen(new SolidColorBrush(Res("BorderColor", Color.FromRgb(0xD3, 0xD8, 0xDE))), 1), new Point(pad, Y(0)), new Point(pad + mw, Y(0)));

        long max = 1;
        foreach (var c in _data)
            if (Geo.ContainsKey(c.CountryCode) && c.Total > max) max = c.Total;

        int shown = 0;
        foreach (var c in _data.OrderByDescending(c => c.Total))
        {
            if (string.IsNullOrEmpty(c.CountryCode) || !Geo.TryGetValue(c.CountryCode, out var g)) continue;
            double frac = (double)c.Total / max;
            double r = 4 + Math.Sqrt(frac) * (Math.Min(mw, mh) * 0.08);
            var p = new Point(X(g.Lon), Y(g.Lat));
            dc.DrawEllipse(bubble, bubbleLine, p, r, r);

            var lbl = new FormattedText($"{c.CountryCode}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10.5, text, dpi);
            dc.DrawText(lbl, new Point(p.X - lbl.Width / 2, p.Y + r + 1));
            shown++;
        }

        if (shown == 0)
        {
            var ft = new FormattedText(
                "No located connections yet.\nInstall the GeoLite2 database for country mapping.",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 13, text, dpi) { TextAlignment = TextAlignment.Center, MaxTextWidth = mw };
            dc.DrawText(ft, new Point(pad, pad + mh / 2 - 16));
        }
        else
        {
            var cap = new FormattedText($"{shown} countries reached", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11, text, dpi);
            dc.DrawText(cap, new Point(pad + 6, pad + 4));
        }
    }

    private static Color Res(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
}
