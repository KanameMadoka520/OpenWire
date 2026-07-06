using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// GlassWire-style timeline strip under the traffic dashboard: a miniature of the
/// whole loaded range with two round drag handles selecting the window the main
/// graph shows. Dragging a handle (or panning the selection) zooms the graph;
/// keeping the right handle pinned at the edge on the live range narrows the live
/// window without freezing it; dragging back to full width resets the view.
/// </summary>
public sealed class TimelineStrip : FrameworkElement
{
    private readonly struct Pt
    {
        public readonly double TimeSec, Combined;
        public Pt(double t, double v) { TimeSec = t; Combined = v; }
    }

    private const double Pad = 14;          // horizontal padding, leaves room for the end handles
    private const double HandleR = 7;

    private readonly List<Pt> _pts = new();
    private bool _live = true;
    private double _rangeSeconds = 300;
    private bool _volumeUnits;

    // Selection as fractions of the current extent; [0,1] = full range.
    private double _selA, _selB = 1;
    private int _drag;            // 0 none, 1 left handle, 2 right handle, 3 pan
    private double _panGrab;      // fraction offset between grab point and _selA while panning

    /// <summary>(fromSec, toSec) — fired on every drag move; drives the cheap zoom preview.</summary>
    public event Action<double, double>? SelectionPreview;

    /// <summary>(fromSec, toSec, isFull, pinnedRight) — fired once on release; commits the view.</summary>
    public event Action<double, double, bool, bool>? SelectionChanged;

    private readonly Brush _fill;
    private readonly Pen _line;
    private readonly Brush _dim;
    private readonly Pen _trackPen, _selTrackPen;
    private readonly Brush _handleFill;
    private readonly Pen _handlePen;
    private readonly Brush _labelBrush;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");
    private double _dpi = 1.0;

    public TimelineStrip()
    {
        Color inC = ResColor("InFillColor", Color.FromRgb(0x2F, 0xB8, 0xC6));
        Color inL = ResColor("InLineColor", Color.FromRgb(0x5F, 0xE3, 0xEF));
        Color panel = ResColor("BgPanelColor", Colors.White);
        Color border = ResColor("BorderColor", Color.FromRgb(0xE4, 0xE7, 0xEB));
        Color accent = ResColor("AccentColor", Color.FromRgb(0x3B, 0x82, 0xF6));
        Color ts = ResColor("TextSecondaryColor", Color.FromRgb(0x5B, 0x64, 0x72));

        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, inC.R, inC.G, inC.B), 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, inC.R, inC.G, inC.B), 1));
        fade.Freeze();
        _fill = fade;
        _line = new Pen(new SolidColorBrush(inL), 1); _line.Freeze();
        _dim = new SolidColorBrush(Color.FromArgb(0xA6, panel.R, panel.G, panel.B)); _dim.Freeze();
        _trackPen = new Pen(new SolidColorBrush(border), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; _trackPen.Freeze();
        _selTrackPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB4, accent.R, accent.G, accent.B)), 3)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; _selTrackPen.Freeze();
        _handleFill = new SolidColorBrush(panel); _handleFill.Freeze();
        _handlePen = new Pen(new SolidColorBrush(ts), 1.3); _handlePen.Freeze();
        _labelBrush = new SolidColorBrush(ResColor("GridTextColor", Color.FromRgb(0x79, 0x81, 0x8B))); _labelBrush.Freeze();

        Loaded += (_, _) => _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    }

    // ---- data ----

    public void SetSeries(TrafficSeries series)
    {
        _pts.Clear();
        _live = series.Range is GraphRange.FiveMinutes;
        _rangeSeconds = series.Range.Duration().TotalSeconds;
        _volumeUnits = series.Range is not (GraphRange.FiveMinutes or GraphRange.ThreeHours);
        double div = _live || _volumeUnits ? 1.0 : series.IntervalSeconds;
        foreach (var s in series.Samples)
            _pts.Add(new Pt(s.Time.ToUnixTimeSeconds(), (s.BytesIn + s.BytesOut) / div));
        _selA = 0; _selB = 1; // a new range always starts at the full view
        InvalidateVisual();
    }

    public void AddSample(double epochSec, double inBytes, double outBytes)
    {
        if (!_live) return;
        double prevLast = _pts.Count > 0 ? _pts[^1].TimeSec : epochSec;
        _pts.Add(new Pt(epochSec, inBytes + outBytes));
        double keep = epochSec - _rangeSeconds - 5;
        while (_pts.Count > 0 && _pts[0].TimeSec < keep) _pts.RemoveAt(0);

        // The live extent slides forward each second. A selection pinned to the
        // right edge keeps its width (still-live narrow window); a frozen one
        // drifts left in fraction space so it stays on the same absolute times.
        if (_drag == 0 && _selB < 0.998 && !(IsFull))
        {
            double span = Math.Max(1, ExtentSpan);
            double shift = (epochSec - prevLast) / span;
            double width = _selB - _selA;
            _selA = Math.Max(0, _selA - shift);
            _selB = Math.Max(_selA + Math.Min(width, 1), Math.Min(1, _selB - shift));
            _selB = Math.Min(1, Math.Max(_selB, _selA + MinSpanFrac));
        }
        InvalidateVisual();
    }

    // ---- selection geometry ----

    private bool IsFull => _selA <= 0.002 && _selB >= 0.998;
    private double ExtentEnd => _pts.Count > 0 ? _pts[^1].TimeSec : 0;
    private double ExtentStart => _live ? ExtentEnd - _rangeSeconds : (_pts.Count > 0 ? _pts[0].TimeSec : 0);
    private double ExtentSpan => Math.Max(1, ExtentEnd - ExtentStart);
    private double MinSpanFrac => Math.Max(0.02, 15.0 / ExtentSpan);

    private double PlotW => Math.Max(1, ActualWidth - 2 * Pad);
    private double FracAt(double x) => Math.Max(0, Math.Min(1, (x - Pad) / PlotW));
    private double XAt(double frac) => Pad + frac * PlotW;

    private void FirePreview()
    {
        if (_pts.Count == 0) return;
        SelectionPreview?.Invoke(ExtentStart + _selA * ExtentSpan, ExtentStart + _selB * ExtentSpan);
    }

    private void FireFinal()
    {
        if (_pts.Count == 0) return;
        double t0 = ExtentStart + _selA * ExtentSpan;
        double t1 = ExtentStart + _selB * ExtentSpan;
        SelectionChanged?.Invoke(t0, t1, IsFull, _selB >= 0.998);
    }

    // ---- input ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_pts.Count == 0) return;
        double x = e.GetPosition(this).X;
        double xa = XAt(_selA), xb = XAt(_selB);
        // The right handle wins ties when the two overlap at the left edge, and
        // vice versa, so a collapsed selection can always be pulled apart.
        if (Math.Abs(x - xb) <= 10 && (Math.Abs(x - xb) <= Math.Abs(x - xa) || x > xb)) _drag = 2;
        else if (Math.Abs(x - xa) <= 10) _drag = 1;
        else if (x > xa && x < xb) { _drag = 3; _panGrab = FracAt(x) - _selA; }
        else if (x < xa) { _drag = 1; _selA = Math.Min(FracAt(x), _selB - MinSpanFrac); }
        else { _drag = 2; _selB = Math.Max(FracAt(x), _selA + MinSpanFrac); }
        CaptureMouse();
        InvalidateVisual();
        FirePreview();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_drag == 0)
        {
            Cursor = Math.Abs(x - XAt(_selA)) <= 10 || Math.Abs(x - XAt(_selB)) <= 10
                ? Cursors.SizeWE
                : (x > XAt(_selA) && x < XAt(_selB) && !IsFull ? Cursors.Hand : Cursors.Arrow);
            return;
        }
        double f = FracAt(x);
        if (_drag == 1) _selA = Math.Min(f, _selB - MinSpanFrac);
        else if (_drag == 2) _selB = Math.Max(f, _selA + MinSpanFrac);
        else
        {
            double width = _selB - _selA;
            double a = Math.Max(0, Math.Min(1 - width, f - _panGrab));
            _selA = a; _selB = a + width;
        }
        InvalidateVisual();
        FirePreview();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == 0) return;
        _drag = 0;
        ReleaseMouseCapture();
        InvalidateVisual();
        FireFinal();
    }

    // ---- rendering ----

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable everywhere

        double chartTop = 2, chartBottom = h - 28;
        double labelTop = h - 24;
        double trackY = h - 8;

        DrawMini(dc, chartTop, chartBottom);

        // Dim what's outside the selection (chart band only).
        double xa = XAt(_selA), xb = XAt(_selB);
        if (_selA > 0.002) dc.DrawRectangle(_dim, null, new Rect(Pad, chartTop, xa - Pad, chartBottom - chartTop));
        if (_selB < 0.998) dc.DrawRectangle(_dim, null, new Rect(xb, chartTop, Pad + PlotW - xb, chartBottom - chartTop));

        DrawLabels(dc, labelTop);

        dc.DrawLine(_trackPen, new Point(Pad, trackY), new Point(Pad + PlotW, trackY));
        dc.DrawLine(_selTrackPen, new Point(xa, trackY), new Point(xb, trackY));
        dc.DrawEllipse(_handleFill, _handlePen, new Point(xa, trackY), HandleR, HandleR);
        dc.DrawEllipse(_handleFill, _handlePen, new Point(xb, trackY), HandleR, HandleR);
    }

    private void DrawMini(DrawingContext dc, double top, double bottom)
    {
        if (_pts.Count < 2) return;
        double t0 = ExtentStart, span = ExtentSpan;
        double peak = 8 * 1024;
        foreach (var p in _pts) peak = Math.Max(peak, p.Combined);
        peak *= 1.12;

        double X(double t) => Pad + (t - t0) / span * PlotW;
        double Y(double v) => top + (bottom - top) * (1 - Math.Min(1.0, v / peak));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(X(_pts[0].TimeSec), bottom), true, true);
            foreach (var p in _pts)
                ctx.LineTo(new Point(X(p.TimeSec), Y(p.Combined)), true, true);
            ctx.LineTo(new Point(X(_pts[^1].TimeSec), bottom), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(_fill, _line, geo);
    }

    private void DrawLabels(DrawingContext dc, double top)
    {
        if (_pts.Count == 0) return;
        int n = Math.Max(2, Math.Min(9, (int)(PlotW / 230) + 2));
        // Short spans need seconds or adjacent ticks collapse into the same minute.
        string fmt = ExtentSpan >= 172800 ? "M/d" : ExtentSpan <= 900 ? "HH:mm:ss" : "HH:mm";
        for (int k = 0; k < n; k++)
        {
            double frac = (double)k / (n - 1);
            double t = ExtentStart + frac * ExtentSpan;
            string s = DateTimeOffset.FromUnixTimeSeconds((long)t).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 10, _labelBrush, _dpi);
            double x = Math.Max(0, Math.Min(ActualWidth - ft.Width, XAt(frac) - ft.Width / 2));
            dc.DrawText(ft, new Point(x, top));
        }
    }

    private static Color ResColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Color c) return c;
        return fallback;
    }
}
