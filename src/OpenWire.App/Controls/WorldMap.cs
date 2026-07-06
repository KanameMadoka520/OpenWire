using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenWire.App.Util;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// Attributes network traffic to its source country. Two interchangeable renderings of
/// the same choropleth (deeper = more traffic):
///   • <see cref="MapMode.Flat"/> — an equirectangular map you can scroll-zoom and
///     drag-pan, with seamless horizontal wraparound.
///   • <see cref="MapMode.Globe"/> — an orthographic 3D globe you can drag-rotate and
///     scroll-zoom (kept label-free — a sphere has no room for codes).
/// Hovering highlights a country and shows its total; clicking raises
/// <see cref="CountrySelected"/> so the host can drill into that country's hosts.
/// Taiwan / Hong Kong / Macao are shown as regions of China (CN-TW / CN-HK / CN-MO).
/// Skin-aware.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    public enum MapMode { Flat, Globe }

    private Dictionary<string, long> _traffic = new(StringComparer.OrdinalIgnoreCase);
    private long _max = 1;
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    private MapMode _mode = MapMode.Flat;

    // Flat-map view state.
    private double _zoom = 1;              // >= 1
    private double _panX, _panY;           // screen-px offset added to the centred origin
    private double _fs, _fox, _foy;        // last computed flat scale + origin (for hit-test)
    private double _fMapW, _fMapH;

    // Globe view state.
    private double _rotLon = 12, _rotLat = 22; // camera centre (deg)
    private double _globeZoom = 1;
    private double _gcx, _gcy, _gR;            // last computed globe centre + radius

    // Interaction.
    private bool _pressed, _dragged;
    private Point _lastPos, _pressPos, _mouse;
    private CountryShape? _hover;
    private Rect _infoRect;   // the ℹ glyph next to the caption; hover shows the one-China note
    private bool _infoHover;
    private readonly Typeface _icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private const double D2R = Math.PI / 180.0;
    private const double MarkerHitPx = 5.5; // marker hover radius, in screen px (matches the drawn dot)

    // Regions too small to be polygons in the 1:110m data — drawn as labelled markers on the flat map.
    private static readonly List<CountryShape> MarkerShapes = BuildMarkers();

    private static List<CountryShape> BuildMarkers()
    {
        var list = new List<CountryShape>();
        // Real SAR coordinates in projection space (x=lon+180, y=90-lat):
        //   Hong Kong ≈ 114.15°E, 22.32°N   Macao ≈ 113.55°E, 22.20°N  (both on the Pearl-River-estuary coast).
        foreach (var (iso, px, py) in new[] { ("HK", 294.15, 67.68), ("MO", 293.55, 67.72) })
        {
            var g = new EllipseGeometry(new Point(px, py), 1.6, 1.6);
            g.Freeze();
            list.Add(new CountryShape
            {
                Iso = iso, Geo = g, Bounds = g.Bounds,
                Centroid = new Point(px, py),                  // equirectangular (flat map)
                CentroidGeo = new Point(px - 180, 90 - py),    // lon/lat (globe)
            });
        }
        return list;
    }

    private static bool IsMarker(CountryShape? s) => s is not null && MarkerShapes.Contains(s);

    private static bool IsChinaIso(string? iso) => (iso ?? "").ToUpperInvariant() is "CN" or "TW" or "HK" or "MO";

    /// <summary>Whether shape <paramref name="s"/> lights up with the currently hovered
    /// one. Hovering any China region (mainland, Taiwan, HK, Macao) highlights the whole
    /// of China together; Taiwan still gets its own label only when hovered directly.</summary>
    private bool HighlightsWith(CountryShape? s)
    {
        if (_hover is null || s is null) return false;
        if (ReferenceEquals(s, _hover)) return true;
        return IsChinaIso(_hover.Iso) && IsChinaIso(s.Iso);
    }

    public MapMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; ResetView(); InvalidateVisual(); } }
    }

    /// <summary>Raised with the raw ISO code of the country the user clicked (e.g. "US", "TW").</summary>
    public event Action<string>? CountrySelected;

    public WorldMap()
    {
        Focusable = true;
        MouseMove += OnMove;
        MouseLeave += OnLeave;
        MouseWheel += OnWheel;
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
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

    /// <summary>Recentre / reset zoom for the current mode.</summary>
    public void ResetView()
    {
        _zoom = 1; _panX = _panY = 0;
        _globeZoom = 1; _rotLon = 12; _rotLat = 22;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo s) { base.OnRenderSizeChanged(s); InvalidateVisual(); }

    // ---- interaction -------------------------------------------------------

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.18 : 1 / 1.18;
        if (_mode == MapMode.Flat)
        {
            var m = e.GetPosition(this);
            LayoutFlat(ActualWidth, ActualHeight);
            double gx = (m.X - _fox) / _fs, gy = (m.Y - _foy) / _fs; // anchor under cursor
            _zoom = Math.Clamp(_zoom * factor, 1, 14);
            LayoutFlat(ActualWidth, ActualHeight);
            _panX += m.X - (_fox + gx * _fs);
            _panY += m.Y - (_foy + gy * _fs);
        }
        else
        {
            _globeZoom = Math.Clamp(_globeZoom * factor, 0.6, 6);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true; _dragged = false;
        _pressPos = _lastPos = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed && !_dragged)
        {
            var hit = HitTest(e.GetPosition(this));
            if (hit is not null && !string.IsNullOrEmpty(hit.Iso))
                CountrySelected?.Invoke(hit.Iso);
        }
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _mouse = p;
        if (_pressed)
        {
            double dx = p.X - _lastPos.X, dy = p.Y - _lastPos.Y;
            if (!_dragged && Math.Abs(p.X - _pressPos.X) + Math.Abs(p.Y - _pressPos.Y) > 4) _dragged = true;
            if (_dragged)
            {
                if (_mode == MapMode.Flat) { _panX += dx; _panY += dy; }
                else
                {
                    _rotLon -= dx * 0.4;
                    _rotLat = Math.Clamp(_rotLat + dy * 0.4, -85, 85);
                }
                _lastPos = p;
                InvalidateVisual();
            }
            return;
        }

        bool overInfo = _infoRect.Contains(p);
        if (overInfo != _infoHover) { _infoHover = overInfo; InvalidateVisual(); }

        var hit = HitTest(p);
        Cursor = hit is not null || overInfo ? Cursors.Hand : Cursors.Arrow;
        if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
        else if (_mode == MapMode.Globe && _hover is not null) InvalidateVisual(); // move tooltip
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        if (_infoHover) { _infoHover = false; InvalidateVisual(); }
        if (_hover is not null) { _hover = null; InvalidateVisual(); }
    }

    private CountryShape? HitTest(Point m)
    {
        if (_mode == MapMode.Flat)
        {
            if (_fs <= 0) return null;
            double gx = (m.X - _fox) / _fs, gy = (m.Y - _foy) / _fs;
            gx -= 360 * Math.Floor(gx / 360); // wrap into [0,360)
            if (gy < 0 || gy > 180) return null;
            // Markers: hit-test in SCREEN pixels and keep the nearest dot, so tightly-packed
            // regions (HK vs MO, ~0.6° apart) each only trigger when the cursor is on them.
            CountryShape? nearest = null;
            double best = MarkerHitPx;
            foreach (var mk in MarkerShapes)
            {
                if (!_traffic.ContainsKey(mk.Iso)) continue;
                double ddx = mk.Centroid.X - gx;
                if (ddx > 180) ddx -= 360; else if (ddx < -180) ddx += 360;
                double dx = ddx * _fs, dy = (mk.Centroid.Y - gy) * _fs;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) { best = d; nearest = mk; }
            }
            if (nearest is not null) return nearest;
            var gp = new Point(gx, gy);
            foreach (var c in WorldGeo.Shapes)
                if (c.Bounds.Contains(gp) && c.Geo.FillContains(gp)) return c;
            return null;
        }

        // Globe: markers first (screen-space nearest dot), then inverse orthographic → polygon.
        if (_gR <= 0) return null;
        double slat0 = Math.Sin(_rotLat * D2R), clat0 = Math.Cos(_rotLat * D2R);
        CountryShape? nearestMk = null;
        double bestMk = MarkerHitPx;
        foreach (var mk in MarkerShapes)
        {
            if (!_traffic.ContainsKey(mk.Iso)) continue;
            var sp = ProjectGlobePoint(mk.CentroidGeo, slat0, clat0, out bool vis);
            if (!vis) continue;
            double ddx = sp.X - m.X, ddy = sp.Y - m.Y;
            double dd = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (dd < bestMk) { bestMk = dd; nearestMk = mk; }
        }
        if (nearestMk is not null) return nearestMk;

        double ux = (m.X - _gcx) / _gR, uy = -(m.Y - _gcy) / _gR;
        double rho = Math.Sqrt(ux * ux + uy * uy);
        if (rho > 1) return null;
        double lat, lon;
        if (rho < 1e-9) { lat = _rotLat; lon = _rotLon; }
        else
        {
            double c = Math.Asin(Math.Min(1, rho)), sinc = Math.Sin(c), cosc = Math.Cos(c);
            lat = Math.Asin(cosc * slat0 + uy * sinc * clat0 / rho) / D2R;
            lon = _rotLon + Math.Atan2(ux * sinc, rho * cosc * clat0 - uy * sinc * slat0) / D2R;
        }
        lon -= 360 * Math.Floor((lon + 180) / 360); // normalise to [-180,180)
        foreach (var c in WorldGeo.Shapes)
            if (c.ContainsGeo(lon, lat)) return c;
        return null;
    }

    // ---- rendering ---------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area hit-testable

        var pal = new Palette(this);
        if (_mode == MapMode.Flat) RenderFlat(dc, w, h, dpi, pal);
        else RenderGlobe(dc, w, h, dpi, pal);

        // shared chrome
        var cap = new FormattedText(string.Format(Loc.S("L.Map.SourceCountriesFmt"), _traffic.Count),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11, pal.Muted, dpi);
        dc.DrawText(cap, new Point(12, 10));
        var infoGlyph = new FormattedText(((char)0xE946).ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _icons, 12, pal.Muted, dpi);
        double ix = 12 + cap.Width + 8, iy = 11;
        dc.DrawText(infoGlyph, new Point(ix, iy));
        _infoRect = new Rect(ix - 4, iy - 4, infoGlyph.Width + 8, infoGlyph.Height + 8);
        if (_infoHover)
        {
            var note = new FormattedText(Loc.S("L.Common.OneChina"),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 11, pal.Primary, dpi)
                { MaxTextWidth = Math.Min(560, Math.Max(200, w - 40)) };
            var bg = new SolidColorBrush(pal.PanelColor) { Opacity = 0.98 };
            dc.DrawRoundedRectangle(bg, new Pen(pal.Coast, 1),
                new Rect(12, iy + infoGlyph.Height + 8, note.Width + 18, note.Height + 12), 5, 5);
            dc.DrawText(note, new Point(21, iy + infoGlyph.Height + 14));
        }
        DrawLegend(dc, 12, h - 22, pal, dpi);
        var hint = new FormattedText(Loc.S(_mode == MapMode.Flat ? "L.Map.HintFlat" : "L.Map.HintGlobe"),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10.5, pal.Muted, dpi);
        dc.DrawText(hint, new Point(w - hint.Width - 12, h - 20));

        if (_hover is not null) DrawTooltip(dc, pal, dpi);
    }

    private void RenderFlat(DrawingContext dc, double w, double h, double dpi, Palette pal)
    {
        LayoutFlat(w, h);
        double lnMax = Math.Log(1 + _max);
        Brush Shade(long bytes, double lo, double hi) => ShadeBrush(pal.Accent, bytes, lo, hi, lnMax);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

        // which horizontal copies cover the viewport (seamless wraparound)
        int kMin = (int)Math.Floor((0 - _fox) / _fMapW) - 1;
        int kMax = (int)Math.Ceiling((w - _fox) / _fMapW) + 1;

        var coast = new Pen(pal.Coast, 0.9 / _fs);
        for (int k = kMin; k <= kMax; k++)
        {
            double shift = k * _fMapW;
            if (_fox + shift > w || _fox + shift + _fMapW < 0) continue;
            dc.PushTransform(new MatrixTransform(_fs, 0, 0, _fs, _fox + shift, _foy));
            foreach (var c in WorldGeo.Shapes)
            {
                Brush fill = _traffic.TryGetValue(c.Iso, out var bytes) && !string.IsNullOrEmpty(c.Iso)
                    ? Shade(bytes, 0.22, 0.92) : pal.Land;
                dc.DrawGeometry(fill, coast, c.Geo);
            }
            if (_hover is not null)
            {
                var hi = new SolidColorBrush(Color.FromArgb(0x2E, pal.Accent.R, pal.Accent.G, pal.Accent.B));
                var hiPen = new Pen(pal.AccentBrush, 2.2 / _fs);
                foreach (var c in WorldGeo.Shapes)
                    if (HighlightsWith(c)) dc.DrawGeometry(hi, hiPen, c.Geo);
            }
            dc.Pop();

            // labels + markers in screen space for this copy
            double ox = _fox + shift;
            foreach (var c in WorldGeo.Shapes)
            {
                if (string.IsNullOrEmpty(c.Iso) || !_traffic.ContainsKey(c.Iso)) continue;
                double lx = ox + c.Centroid.X * _fs, ly = _foy + c.Centroid.Y * _fs;
                if (lx < -20 || lx > w + 20) continue;
                var ft = new FormattedText(OneChina.DisplayCode(c.Iso), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _ui, 10, pal.Strong, dpi);
                dc.DrawText(ft, new Point(lx - ft.Width / 2, ly - ft.Height / 2));
            }
            foreach (var mk in MarkerShapes)
            {
                if (!_traffic.TryGetValue(mk.Iso, out var bytes) || bytes <= 0) continue;
                double mx = ox + mk.Centroid.X * _fs, my = _foy + mk.Centroid.Y * _fs;
                if (mx < -20 || mx > w + 20) continue;
                var pt = new Point(mx, my);
                double r = 3.2;
                dc.DrawEllipse(Shade(bytes, 0.5, 0.98), new Pen(pal.AccentBrush, 1), pt, r, r);
                if (HighlightsWith(mk))
                    dc.DrawEllipse(null, new Pen(pal.AccentBrush, 2), pt, r + 2.5, r + 2.5);
                var ft = new FormattedText(OneChina.DisplayCode(mk.Iso), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _ui, 9.5, pal.Strong, dpi);
                // HK's label sits to the right, Macao's to the left, so the two never collide.
                var lp = mk.Iso == "HK"
                    ? new Point(pt.X + r + 3, pt.Y - ft.Height / 2)
                    : new Point(pt.X - ft.Width - r - 3, pt.Y - ft.Height / 2);
                dc.DrawText(ft, lp);
            }
        }
        dc.Pop(); // clip
    }

    private void RenderGlobe(DrawingContext dc, double w, double h, double dpi, Palette pal)
    {
        _gcx = w / 2; _gcy = h / 2;
        _gR = Math.Min(w, h) / 2 * 0.92 * _globeZoom;
        double lnMax = Math.Log(1 + _max);
        var centre = new Point(_gcx, _gcy);

        // ocean disc with a soft radial shade for depth
        var ocean = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.62,
        };
        ocean.GradientStops.Add(new GradientStop(pal.OceanLight, 0));
        ocean.GradientStops.Add(new GradientStop(pal.OceanDark, 1));
        ocean.Freeze();
        dc.DrawEllipse(ocean, new Pen(pal.Coast, 1.1), centre, _gR, _gR);

        dc.PushClip(new EllipseGeometry(centre, _gR, _gR));

        double slat0 = Math.Sin(_rotLat * D2R), clat0 = Math.Cos(_rotLat * D2R);
        DrawGraticule(dc, pal, slat0, clat0);

        var coast = new Pen(pal.Coast, 0.5);
        foreach (var c in WorldGeo.Shapes)
        {
            var geo = ProjectGlobe(c, slat0, clat0);
            if (geo is null) continue;
            Brush fill = _traffic.TryGetValue(c.Iso, out var bytes) && !string.IsNullOrEmpty(c.Iso)
                ? LandShade(pal.LandBase, pal.Accent, bytes, lnMax) : pal.LandGlobe;
            dc.DrawGeometry(fill, coast, geo);
        }
        if (_hover is not null)
        {
            var hi = new SolidColorBrush(Color.FromArgb(0x3A, pal.Accent.R, pal.Accent.G, pal.Accent.B));
            var hiPen = new Pen(pal.AccentBrush, 1.6);
            foreach (var c in WorldGeo.Shapes)
            {
                if (!HighlightsWith(c)) continue;
                var geo = ProjectGlobe(c, slat0, clat0);
                if (geo is not null) dc.DrawGeometry(hi, hiPen, geo);
            }
        }

        // HK / MO as dots on the globe too (no labels — a sphere has no room for codes).
        foreach (var mk in MarkerShapes)
        {
            if (!_traffic.TryGetValue(mk.Iso, out var bytes) || bytes <= 0) continue;
            var sp = ProjectGlobePoint(mk.CentroidGeo, slat0, clat0, out bool vis);
            if (!vis) continue;
            const double r = 3.0;
            dc.DrawEllipse(ShadeBrush(pal.Accent, bytes, 0.5, 0.98, lnMax), new Pen(pal.AccentBrush, 1), sp, r, r);
            if (HighlightsWith(mk))
                dc.DrawEllipse(null, new Pen(pal.AccentBrush, 2), sp, r + 2.5, r + 2.5);
        }
        dc.Pop(); // clip
    }

    private Geometry? ProjectGlobe(CountryShape c, double slat0, double clat0)
    {
        var sg = new StreamGeometry { FillRule = FillRule.Nonzero };
        bool any = false;
        using (var ctx = sg.Open())
        {
            foreach (var ring in c.GlobeRings)
            {
                var pts = new List<Point>(ring.Length);
                bool ringVisible = false;
                foreach (var ll in ring)
                {
                    pts.Add(ProjectGlobePoint(ll, slat0, clat0, out bool vis));
                    ringVisible |= vis;
                }
                if (!ringVisible || pts.Count < 3) continue;
                any = true;
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, false);
            }
        }
        if (!any) return null;
        sg.Freeze();
        return sg;
    }

    private Point ProjectGlobePoint(Point ll, double slat0, double clat0, out bool visible)
    {
        double lam = (ll.X - _rotLon) * D2R, phi = ll.Y * D2R;
        double cphi = Math.Cos(phi), sphi = Math.Sin(phi), clam = Math.Cos(lam), slam = Math.Sin(lam);
        double cosc = slat0 * sphi + clat0 * cphi * clam;
        double x = cphi * slam;
        double y = clat0 * sphi - slat0 * cphi * clam;
        visible = cosc >= 0;
        if (!visible)
        {
            double d = Math.Sqrt(x * x + y * y);
            if (d > 1e-9) { x /= d; y /= d; } // clamp behind-the-horizon points onto the limb
        }
        return new Point(_gcx + x * _gR, _gcy - y * _gR);
    }

    private void DrawGraticule(DrawingContext dc, Palette pal, double slat0, double clat0)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x1E, pal.Coast.Color.R, pal.Coast.Color.G, pal.Coast.Color.B)), 0.6);
        for (int lat = -60; lat <= 60; lat += 30)
        {
            var fig = BuildArc(lon => new Point(lon, lat), -180, 180, slat0, clat0);
            if (fig is not null) dc.DrawGeometry(null, pen, fig);
        }
        for (int lon = -150; lon <= 180; lon += 30)
        {
            var fig = BuildArc(lat => new Point(lon, lat), -85, 85, slat0, clat0);
            if (fig is not null) dc.DrawGeometry(null, pen, fig);
        }
    }

    private Geometry? BuildArc(Func<double, Point> at, double from, double to, double slat0, double clat0)
    {
        var sg = new StreamGeometry();
        bool started = false, any = false;
        using (var ctx = sg.Open())
        {
            for (double t = from; t <= to + 0.01; t += 4)
            {
                var p = ProjectGlobePoint(at(t), slat0, clat0, out bool vis);
                if (!vis) { started = false; continue; }
                if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                else ctx.LineTo(p, true, false);
                any = true;
            }
        }
        if (!any) return null;
        sg.Freeze();
        return sg;
    }

    private void LayoutFlat(double w, double h)
    {
        double pad = 8, availW = w - 2 * pad, availH = h - 2 * pad;
        double baseS = Math.Min(availW / 360.0, availH / 180.0);
        _fs = baseS * _zoom;
        _fMapW = 360 * _fs; _fMapH = 180 * _fs;
        double baseOx = pad + (availW - _fMapW) / 2, baseOy = pad + (availH - _fMapH) / 2;
        _fox = baseOx + _panX;
        _foy = baseOy + _panY;
        if (_fMapH <= availH) { _foy = baseOy; _panY = 0; }
        else
        {
            _foy = Math.Clamp(_foy, pad + availH - _fMapH, pad);
            _panY = _foy - baseOy;
        }
    }

    private static Brush ShadeBrush(Color accent, long bytes, double lo, double hi, double lnMax)
    {
        double t = lnMax > 0 ? Math.Log(1 + bytes) / lnMax : 0;
        byte a = (byte)(255 * (lo + (hi - lo) * t));
        var b = new SolidColorBrush(Color.FromArgb(a, accent.R, accent.G, accent.B));
        b.Freeze();
        return b;
    }

    /// <summary>Opaque land fill that deepens from the pale <paramref name="baseColor"/>
    /// toward the accent as traffic grows — so the globe reads light-red at rest and
    /// saturates red with more traffic, over the neutral grey ocean.</summary>
    private static Brush LandShade(Color baseColor, Color accent, long bytes, double lnMax)
    {
        double t = lnMax > 0 ? Math.Log(1 + bytes) / lnMax : 0;
        double f = 0.22 + 0.78 * t; // any traffic is clearly deeper than the base
        var c = Color.FromRgb(
            (byte)(baseColor.R + (accent.R - baseColor.R) * f),
            (byte)(baseColor.G + (accent.G - baseColor.G) * f),
            (byte)(baseColor.B + (accent.B - baseColor.B) * f));
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void DrawLegend(DrawingContext dc, double x, double y, Palette pal, double dpi)
    {
        const double lw = 120, lh = 8;
        var grad = new LinearGradientBrush(
            Color.FromArgb(0x38, pal.Accent.R, pal.Accent.G, pal.Accent.B),
            Color.FromArgb(0xE6, pal.Accent.R, pal.Accent.G, pal.Accent.B), 0);
        dc.DrawRectangle(grad, new Pen(pal.Border, 0.8), new Rect(x, y, lw, lh));
        var less = new FormattedText(Loc.S("L.Map.LegendLess"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10, pal.Muted, dpi);
        var more = new FormattedText(Loc.S("L.Map.LegendMore"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 10, pal.Muted, dpi);
        dc.DrawText(less, new Point(x, y - 14));
        dc.DrawText(more, new Point(x + lw - more.Width, y - 14));
    }

    private void DrawTooltip(DrawingContext dc, Palette pal, double dpi)
    {
        var hover = _hover!;
        string name = CountryName.Localized(hover.Iso, hover.Name);
        string info = _traffic.TryGetValue(hover.Iso, out var b)
            ? string.Format(Loc.S("L.Map.ReceivedFmt"), ByteFormatter.Bytes(b))
            : Loc.S("L.Map.NoTraffic");

        var l1 = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _ui, 12.5, pal.Primary, dpi);
        var l2 = new FormattedText(info, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 11.5, pal.AccentBrush, dpi);
        double cw = Math.Max(l1.Width, l2.Width) + 18, ch = l1.Height + l2.Height + 12;

        double cx = _mouse.X + 14, cy = _mouse.Y - ch - 8;
        if (cx + cw > ActualWidth) cx = ActualWidth - cw - 4;
        if (cx < 2) cx = 2;
        if (cy < 2) cy = _mouse.Y + 16;

        var bg = new SolidColorBrush(pal.PanelColor) { Opacity = 0.98 };
        dc.DrawRoundedRectangle(bg, new Pen(pal.Coast, 1), new Rect(cx, cy, cw, ch), 5, 5);
        dc.DrawText(l1, new Point(cx + 9, cy + 5));
        dc.DrawText(l2, new Point(cx + 9, cy + 5 + l1.Height));
    }

    /// <summary>Skin-aware colours pulled once per render.</summary>
    private sealed class Palette
    {
        public readonly Color Accent, PanelColor, OceanLight, OceanDark, LandBase;
        private readonly Color _oceanBase;
        public readonly SolidColorBrush AccentBrush, Land, LandGlobe, Coast, Border, Muted, Strong, Primary;

        public Palette(FrameworkElement e)
        {
            Accent = Res("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));
            PanelColor = Res("BgPanelColor", Colors.White);
            AccentBrush = Frozen(Accent);
            Land = Frozen(Res("BgSubtleColor", Color.FromRgb(0xEC, 0xEE, 0xF1)));
            // Ocean = the theme's subtle background DESATURATED to a neutral grey — this
            // drops the accent's red (which made the globe alarming) while tracking the
            // theme's darkness, so it's a light grey in day skins and a dark grey in the
            // Berry-night dark skin instead of an out-of-place bright disc.
            _oceanBase = Desaturate(Res("BgSubtleColor", Color.FromRgb(0xE1, 0xE5, 0xEA)), 0.88);
            OceanDark = _oceanBase;
            OceanLight = Blend(_oceanBase, Colors.White, 0.10);
            // Globe land base: the ocean tone nudged toward the accent (a muted red in the
            // Berry skins, muted blue elsewhere) that deepens toward the accent with
            // traffic — so it tracks the theme's darkness rather than glowing pale.
            LandBase = Blend(_oceanBase, Accent, 0.30);
            LandGlobe = Frozen(LandBase);
            Coast = Frozen(Res("BorderStrongColor", Color.FromRgb(0x8B, 0x92, 0x9B)));
            Border = Frozen(Res("BorderColor", Color.FromRgb(0xD3, 0xD8, 0xDE)));
            Muted = Frozen(Res("TextMutedColor", Color.FromRgb(0x79, 0x81, 0x8B)));
            Strong = Frozen(Res("TextSecondaryColor", Color.FromRgb(0x52, 0x59, 0x63)));
            Primary = Frozen(Res("TextPrimaryColor", Color.FromRgb(0x23, 0x27, 0x2D)));
        }

        private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Pull a colour toward its own grey (luminance), dropping saturation.</summary>
        private static Color Desaturate(Color c, double amount)
        {
            byte g = (byte)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
            return Color.FromRgb(
                (byte)(c.R + (g - c.R) * amount),
                (byte)(c.G + (g - c.G) * amount),
                (byte)(c.B + (g - c.B) * amount));
        }

        private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static Color Res(string key, Color fallback)
            => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
    }
}
