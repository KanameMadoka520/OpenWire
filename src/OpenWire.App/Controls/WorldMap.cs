using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// A choropleth world map (Natural Earth country polygons, public domain) that
/// attributes traffic to its source country: every country is its own region, shaded
/// by how much traffic came from it (deeper = more), with hover highlighting the
/// country's shape and a tooltip. Hong Kong / Macao / Taiwan are shown as regions of
/// China (labelled CN-HK / CN-MO / CN-TW). Skin-aware.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private Dictionary<string, long> _traffic = new(StringComparer.OrdinalIgnoreCase);
    private long _max = 1;
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    private double _s = 1, _ox, _oy;   // projection: screen = (origin + geom * scale)
    private CountryShape? _hover;

    // Regions too small to be polygons in the 1:110m data — drawn as labelled markers.
    private static readonly List<CountryShape> MarkerShapes = BuildMarkers();

    private static List<CountryShape> BuildMarkers()
    {
        var list = new List<CountryShape>();
        // projected (x=lon+180, y=90-lat); MO nudged slightly for label legibility.
        foreach (var (iso, px, py) in new[] { ("HK", 294.2, 67.7), ("MO", 292.2, 69.2) })
        {
            var g = new EllipseGeometry(new Point(px, py), 2.4, 2.4);
            g.Freeze();
            list.Add(new CountryShape { Iso = iso, Geo = g, Bounds = g.Bounds, Centroid = new Point(px, py) });
        }
        return list;
    }

    // One-China display labels: Taiwan / Hong Kong / Macao are regions of China.
    private static string DisplayIso(string iso) => iso switch
    {
        "TW" => "CN-TW", "HK" => "CN-HK", "MO" => "CN-MO", _ => iso,
    };

    private static string DisplayName(string iso, string fallback) => iso switch
    {
        "TW" => "Taiwan, China",
        "HK" => "Hong Kong, China",
        "MO" => "Macao, China",
        _ => string.IsNullOrEmpty(fallback) ? iso : fallback,
    };

    private static bool IsMarker(CountryShape? s) => s is not null && MarkerShapes.Contains(s);

    public WorldMap()
    {
        MouseMove += OnMove;
        MouseLeave += (_, _) => { if (_hover is not null) { _hover = null; InvalidateVisual(); } };
    }

    public void SetData(IReadOnlyList<CountryUsage> data)
    {
        _traffic = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in data ?? Array.Empty<CountryUsage>())
            if (!string.IsNullOrEmpty(c.CountryCode) && c.Total > 0)
                _traffic[c.CountryCode] = c.Total;
        _max = 1;
        foreach (var v in _traffic.Values) if (v > _max) _max = v;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo s) { base.OnRenderSizeChanged(s); InvalidateVisual(); }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var m = e.GetPosition(this);
        var gp = new Point((m.X - _ox) / _s, (m.Y - _oy) / _s); // inverse projection
        CountryShape? hit = null;
        // markers first (small, sit on top of China)
        foreach (var mk in MarkerShapes)
            if (_traffic.ContainsKey(mk.Iso) && mk.Bounds.Contains(gp) && mk.Geo.FillContains(gp)) { hit = mk; break; }
        if (hit is null)
            foreach (var c in WorldGeo.Shapes)
            {
                if (!c.Bounds.Contains(gp)) continue;
                if (c.Geo.FillContains(gp)) { hit = c; break; }
            }
        if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area hit-testable

        var landFill = new SolidColorBrush(Res("BgSubtleColor", Color.FromRgb(0xEC, 0xEE, 0xF1)));
        var coast = new Pen(new SolidColorBrush(Res("BorderStrongColor", Color.FromRgb(0x8B, 0x92, 0x9B))), 1);
        var muted = new SolidColorBrush(Res("TextMutedColor", Color.FromRgb(0x79, 0x81, 0x8B)));
        var strong = new SolidColorBrush(Res("TextSecondaryColor", Color.FromRgb(0x52, 0x59, 0x63)));
        var primary = new SolidColorBrush(Res("TextPrimaryColor", Color.FromRgb(0x23, 0x27, 0x2D)));
        Color accent = Res("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));

        double pad = 10, availW = w - 2 * pad, availH = h - 2 * pad;
        _s = Math.Min(availW / 360.0, availH / 180.0);
        double mapW = 360 * _s, mapH = 180 * _s;
        _ox = pad + (availW - mapW) / 2;
        _oy = pad + (availH - mapH) / 2;

        double Sx(double gx) => _ox + gx * _s;
        double Sy(double gy) => _oy + gy * _s;
        double lnMax = Math.Log(1 + _max);
        Brush Shade(long bytes, double lo, double hi)
        {
            double t = lnMax > 0 ? Math.Log(1 + bytes) / lnMax : 0;
            byte a = (byte)(255 * (lo + (hi - lo) * t));
            return new SolidColorBrush(Color.FromArgb(a, accent.R, accent.G, accent.B));
        }

        // choropleth: fill each country (deeper accent = more traffic)
        dc.PushTransform(new MatrixTransform(_s, 0, 0, _s, _ox, _oy));
        coast.Thickness = 0.9 / _s;
        foreach (var c in WorldGeo.Shapes)
        {
            Brush fill = !string.IsNullOrEmpty(c.Iso) && _traffic.TryGetValue(c.Iso, out var bytes)
                ? Shade(bytes, 0.22, 0.92) : landFill;
            dc.DrawGeometry(fill, coast, c.Geo);
        }
        if (_hover is not null && !IsMarker(_hover))
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
                new Pen(new SolidColorBrush(accent), 2.4 / _s), _hover.Geo);
        dc.Pop();

        // ISO labels for source countries (one-China labels for TW etc.)
        foreach (var c in WorldGeo.Shapes)
        {
            if (string.IsNullOrEmpty(c.Iso) || !_traffic.ContainsKey(c.Iso)) continue;
            var ft = new FormattedText(DisplayIso(c.Iso), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10, strong, dpi);
            dc.DrawText(ft, new Point(Sx(c.Centroid.X) - ft.Width / 2, Sy(c.Centroid.Y) - ft.Height / 2));
        }

        // HK / MO markers (no polygon at this scale)
        foreach (var mk in MarkerShapes)
        {
            if (!_traffic.TryGetValue(mk.Iso, out var bytes) || bytes <= 0) continue;
            var p = new Point(Sx(mk.Centroid.X), Sy(mk.Centroid.Y));
            double r = 4.5;
            dc.DrawEllipse(Shade(bytes, 0.4, 0.95), new Pen(new SolidColorBrush(accent), 1), p, r, r);
            if (ReferenceEquals(_hover, mk))
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(accent), 2), p, r + 2.5, r + 2.5);
            var ft = new FormattedText(DisplayIso(mk.Iso), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 9.5, strong, dpi);
            dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y + r + 1));
        }

        // caption + legend
        var cap = new FormattedText($"{_traffic.Count} source countries/regions", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11, muted, dpi);
        dc.DrawText(cap, new Point(_ox + 6, _oy + 4));
        DrawLegend(dc, _ox + 6, _oy + mapH - 22, accent, muted, dpi);

        if (_hover is not null) DrawTooltip(dc, Sx, Sy, accent, primary, dpi);
    }

    private void DrawLegend(DrawingContext dc, double x, double y, Color accent, Brush text, double dpi)
    {
        const double lw = 120, lh = 8;
        var grad = new LinearGradientBrush(
            Color.FromArgb(0x38, accent.R, accent.G, accent.B),
            Color.FromArgb(0xE6, accent.R, accent.G, accent.B), 0);
        var border = new Pen(new SolidColorBrush(Res("BorderColor", Color.FromRgb(0xD3, 0xD8, 0xDE))), 0.8);
        dc.DrawRectangle(grad, border, new Rect(x, y, lw, lh));
        var less = new FormattedText("less", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10, text, dpi);
        var more = new FormattedText("more traffic", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10, text, dpi);
        dc.DrawText(less, new Point(x, y - 14));
        dc.DrawText(more, new Point(x + lw - more.Width, y - 14));
    }

    private void DrawTooltip(DrawingContext dc, Func<double, double> Sx, Func<double, double> Sy, Color accent, Brush primary, double dpi)
    {
        var hover = _hover!;
        string name = DisplayName(hover.Iso, hover.Name);
        string info = !string.IsNullOrEmpty(hover.Iso) && _traffic.TryGetValue(hover.Iso, out var b)
            ? ByteFormatter.Bytes(b) + " received" : "no recorded traffic";

        var l1 = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 12.5, primary, dpi);
        var l2 = new FormattedText(info, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11.5, new SolidColorBrush(accent), dpi);
        double cw = Math.Max(l1.Width, l2.Width) + 18, ch = l1.Height + l2.Height + 12;

        double ax = Sx(hover.Centroid.X), ay = Sy(hover.Centroid.Y);
        double cx = ax + 10, cy = ay - ch - 8;
        if (cx + cw > ActualWidth) cx = ActualWidth - cw - 4;
        if (cx < 2) cx = 2;
        if (cy < 2) cy = ay + 10;

        var bg = new SolidColorBrush(Res("BgPanelColor", Colors.White)) { Opacity = 0.98 };
        var border = new Pen(new SolidColorBrush(Res("BorderStrongColor", Color.FromRgb(0x8B, 0x92, 0x9B))), 1);
        dc.DrawRoundedRectangle(bg, border, new Rect(cx, cy, cw, ch), 5, 5);
        dc.DrawText(l1, new Point(cx + 9, cy + 5));
        dc.DrawText(l2, new Point(cx + 9, cy + 5 + l1.Height));
    }

    private static Color Res(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
}
