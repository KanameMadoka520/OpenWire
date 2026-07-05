using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Models;

namespace OpenWire.App.Controls;

/// <summary>
/// A real world map (Natural Earth coastline, public domain) that attributes traffic
/// to its source countries: continents are drawn with an equirectangular projection
/// and a bubble is placed on each country that exchanged traffic, sized by its share.
/// Skin-aware; shows a note when GeoIP has located nothing.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private IReadOnlyList<CountryUsage> _data = Array.Empty<CountryUsage>();
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    public void SetData(IReadOnlyList<CountryUsage> data) { _data = data ?? Array.Empty<CountryUsage>(); InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo s) { base.OnRenderSizeChanged(s); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var landFill = new SolidColorBrush(Res("BgSubtleColor", Color.FromRgb(0xEC, 0xEE, 0xF1)));
        var coast = new Pen(new SolidColorBrush(Res("BorderStrongColor", Color.FromRgb(0x8B, 0x92, 0x9B))), 1);
        var grid = new Pen(new SolidColorBrush(Res("GridLineColor", Color.FromRgb(0xE4, 0xE7, 0xEB))), 1) { DashStyle = new DashStyle(new double[] { 1, 4 }, 0) };
        var text = new SolidColorBrush(Res("TextMutedColor", Color.FromRgb(0x79, 0x81, 0x8B)));
        var strong = new SolidColorBrush(Res("TextSecondaryColor", Color.FromRgb(0x52, 0x59, 0x63)));
        Color accent = Res("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));
        var bubble = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        var bubbleLine = new Pen(new SolidColorBrush(accent), 1.3);

        // Letterboxed equirectangular projection (keep the 2:1 aspect, centred).
        double pad = 10, availW = w - 2 * pad, availH = h - 2 * pad;
        double s = Math.Min(availW / 360.0, availH / 180.0);
        double mapW = 360 * s, mapH = 180 * s;
        double ox = pad + (availW - mapW) / 2, oy = pad + (availH - mapH) / 2;
        double X(double lon) => ox + (lon + 180) * s;
        double Y(double lat) => oy + (90 - lat) * s;

        // graticule every 30°
        for (int lon = -150; lon <= 150; lon += 30) dc.DrawLine(grid, new Point(X(lon), oy), new Point(X(lon), oy + mapH));
        for (int lat = -60; lat <= 60; lat += 30) dc.DrawLine(grid, new Point(ox, Y(lat)), new Point(ox + mapW, Y(lat)));

        // continents (geometry is in [0,360]x[0,180]; scale into the map rect)
        var g = WorldGeo.Land;
        if (g != Geometry.Empty)
        {
            dc.PushTransform(new MatrixTransform(s, 0, 0, s, ox, oy));
            coast.Thickness = 0.9 / s; // ~1px after scaling
            dc.DrawGeometry(landFill, coast, g);
            dc.Pop();
        }

        // bubbles
        long max = 1;
        foreach (var c in _data)
            if (WorldGeo.Centroids.ContainsKey(c.CountryCode) && c.Total > max) max = c.Total;

        int shown = 0;
        foreach (var c in _data.OrderByDescending(c => c.Total))
        {
            if (string.IsNullOrEmpty(c.CountryCode) || !WorldGeo.Centroids.TryGetValue(c.CountryCode, out var g2)) continue;
            double frac = (double)c.Total / max;
            double r = 3.5 + Math.Sqrt(frac) * (Math.Min(mapW, mapH) * 0.09);
            var p = new Point(X(g2.Lon), Y(g2.Lat));
            dc.DrawEllipse(bubble, bubbleLine, p, r, r);
            var lbl = new FormattedText(c.CountryCode, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10.5, strong, dpi);
            dc.DrawText(lbl, new Point(p.X - lbl.Width / 2, p.Y + r));
            shown++;
        }

        if (shown == 0)
        {
            var ft = new FormattedText(
                "No located connections yet.\nInstall the GeoLite2 database to attribute traffic to countries.",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 13, text, dpi) { TextAlignment = TextAlignment.Center, MaxTextWidth = availW };
            dc.DrawText(ft, new Point(pad, oy + mapH / 2 - 16));
        }
        else
        {
            var cap = new FormattedText($"{shown} source countries", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11, text, dpi);
            dc.DrawText(cap, new Point(ox + 6, oy + 4));
        }
    }

    private static Color Res(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
}
