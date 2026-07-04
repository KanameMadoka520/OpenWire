using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// The signature OpenWire graph: a live, smoothly-scrolling filled-area chart of
/// incoming (teal) and outgoing (amber) throughput, with auto-scaling, gridlines
/// and an on-canvas scale readout. Fed per-second in live mode, or a full
/// <see cref="TrafficSeries"/> for historical ranges.
/// </summary>
public sealed class TrafficGraph : FrameworkElement
{
    private readonly struct Pt
    {
        public readonly double TimeSec, In, Out;
        public Pt(double t, double i, double o) { TimeSec = t; In = i; Out = o; }
    }

    private readonly List<Pt> _points = new();
    private double _peak = 8 * 1024;
    private double _renderPeak = 8 * 1024;
    private bool _volumeUnits;

    private readonly Brush _inFill, _outFill;
    private readonly Pen _inPen, _outPen, _gridPen;
    private readonly Brush _gridText;
    private readonly Brush _idleFill;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    private DateTime _lastFrame = DateTime.MinValue;

    public double WindowSeconds { get; private set; } = 300;
    public bool LiveScroll { get; private set; } = true;

    public TrafficGraph()
    {
        Color inC = ResColor("InFillColor", Color.FromRgb(0x2F, 0xB8, 0xC6));
        Color inL = ResColor("InLineColor", Color.FromRgb(0x5F, 0xE3, 0xEF));
        Color outC = ResColor("OutFillColor", Color.FromRgb(0xE8, 0xA1, 0x3A));
        Color outL = ResColor("OutLineColor", Color.FromRgb(0xFF, 0xC4, 0x6B));

        _inFill = VerticalFade(inC, 0.72, 0.10);
        _outFill = VerticalFade(outC, 0.68, 0.08);
        _inPen = new Pen(new SolidColorBrush(inL), 1.4); _inPen.Freeze();
        _outPen = new Pen(new SolidColorBrush(outL), 1.4); _outPen.Freeze();
        _gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)), 1); _gridPen.Freeze();
        _gridText = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)); _gridText.Freeze();
        _idleFill = new SolidColorBrush(Color.FromArgb(0x16, 0x94, 0x9E, 0xAD)); _idleFill.Freeze();

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!LiveScroll) return;
        var now = DateTime.UtcNow;
        if ((now - _lastFrame).TotalMilliseconds < 33) return; // ~30 fps
        _lastFrame = now;
        InvalidateVisual();
    }

    /// <summary>Push a single live sample (bytes in the last second == bytes/sec).</summary>
    public void AddSample(double epochSec, double inBytes, double outBytes)
    {
        _points.Add(new Pt(epochSec, inBytes, outBytes));
        double cutoff = epochSec - WindowSeconds - 5;
        while (_points.Count > 0 && _points[0].TimeSec < cutoff) _points.RemoveAt(0);
    }

    /// <summary>Load a full historical series (switches to static layout for long ranges).</summary>
    public void SetSeries(TrafficSeries series)
    {
        _points.Clear();
        bool live = series.Range is GraphRange.FiveMinutes;
        LiveScroll = live;
        WindowSeconds = series.Range.Duration().TotalSeconds;
        _volumeUnits = series.Range is not (GraphRange.FiveMinutes or GraphRange.ThreeHours);

        double div = live ? 1.0 : series.IntervalSeconds; // per-interval bytes -> rate unless volume
        foreach (var s in series.Samples)
        {
            double t = s.Time.ToUnixTimeSeconds();
            double scale = _volumeUnits ? 1.0 : 1.0 / div;
            _points.Add(new Pt(t, s.BytesIn * scale, s.BytesOut * scale));
        }
        InvalidateVisual();
    }

    public void Clear() { _points.Clear(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double nowSec = LiveScroll
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            : (_points.Count > 0 ? _points[^1].TimeSec : 0);
        double fromSec = nowSec - WindowSeconds;

        // target peak from visible points (overlaid, so scale to the taller direction)
        double target = 8 * 1024;
        foreach (var p in _points)
            if (p.TimeSec >= fromSec) target = Math.Max(target, Math.Max(p.In, p.Out));
        target *= 1.18;
        _peak = target;
        _renderPeak += (_peak - _renderPeak) * 0.15; // eased rescale
        double peak = Math.Max(1024, _renderPeak);

        const double padTop = 14, padBottom = 6;
        double plotH = h - padTop - padBottom;
        double X(double t) => (t - fromSec) / WindowSeconds * w;
        double Y(double v) => padTop + plotH - Math.Min(1.0, v / peak) * plotH;

        DrawGridlines(dc, w, h, peak, padTop, plotH);

        // Idle shading: on historical ranges, wash the spans where nothing was
        // recorded (PC asleep / no activity) — GlassWire's grey "no-data" bands.
        if (!LiveScroll) DrawIdleBands(dc, X, padTop, plotH, fromSec, peak);

        DrawArea(dc, X, Y, h - padBottom, p => p.Out, _outFill, _outPen, fromSec);
        DrawArea(dc, X, Y, h - padBottom, p => p.In, _inFill, _inPen, fromSec);

        // scale readout, upper-left
        string label = _volumeUnits ? ByteFormatter.Bytes((long)peak) : ByteFormatter.Rate(peak);
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _mono, 12, _gridText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(8, 6));
    }

    private void DrawGridlines(DrawingContext dc, double w, double h, double peak, double padTop, double plotH)
    {
        int lines = 4;
        for (int i = 1; i <= lines; i++)
        {
            double frac = (double)i / lines;
            double y = padTop + plotH - frac * plotH;
            dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            double val = peak * frac;
            string s = _volumeUnits ? ByteFormatter.Bytes((long)val) : ByteFormatter.Rate(val);
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _mono, 10, _gridText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(w - ft.Width - 6, y + 1));
        }
    }

    /// <summary>Shade contiguous idle spans (combined throughput below a small floor).</summary>
    private void DrawIdleBands(DrawingContext dc, Func<double, double> X, double top, double plotH, double fromSec, double peak)
    {
        var vis = new List<Pt>(_points.Count);
        foreach (var p in _points) if (p.TimeSec >= fromSec - 5) vis.Add(p);
        if (vis.Count < 2) return;

        double floor = Math.Max(64, peak * 0.02);
        int i = 0;
        while (i < vis.Count)
        {
            if (vis[i].In + vis[i].Out <= floor)
            {
                int j = i;
                while (j + 1 < vis.Count && vis[j + 1].In + vis[j + 1].Out <= floor) j++;
                double x0 = X(vis[i].TimeSec);
                double x1 = X(vis[Math.Min(j + 1, vis.Count - 1)].TimeSec);
                if (x1 - x0 >= 2)
                    dc.DrawRectangle(_idleFill, null, new Rect(x0, top, x1 - x0, plotH));
                i = j + 1;
            }
            else i++;
        }
    }

    private void DrawArea(DrawingContext dc, Func<double, double> X, Func<double, double> Y, double baseline,
        Func<Pt, double> value, Brush fill, Pen pen, double fromSec)
    {
        var visible = new List<Pt>(_points.Count);
        foreach (var p in _points) if (p.TimeSec >= fromSec - 5) visible.Add(p);
        if (visible.Count < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(X(visible[0].TimeSec), baseline), true, true);
            ctx.LineTo(new Point(X(visible[0].TimeSec), Y(value(visible[0]))), true, false);
            for (int i = 1; i < visible.Count; i++)
                ctx.LineTo(new Point(X(visible[i].TimeSec), Y(value(visible[i]))), true, true);
            ctx.LineTo(new Point(X(visible[^1].TimeSec), baseline), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(visible[0].TimeSec), Y(value(visible[0]))), false, false);
            for (int i = 1; i < visible.Count; i++)
                ctx.LineTo(new Point(X(visible[i].TimeSec), Y(value(visible[i]))), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static LinearGradientBrush VerticalFade(Color c, double topA, double bottomA)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(topA * 255), c.R, c.G, c.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(bottomA * 255), c.R, c.G, c.B), 1));
        b.Freeze();
        return b;
    }

    private static Color ResColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Color c) return c;
        return fallback;
    }
}
