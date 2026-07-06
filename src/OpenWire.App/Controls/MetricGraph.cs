using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// A single-series filled line graph for the Hardware Resources screen
/// (CPU / memory / disk / GPU). Auto-scales to the visible peak and prints the
/// scale on the right, GlassWire-style.
///
/// Rendering follows the same three-layer split as <see cref="TrafficGraph"/> so
/// the live scroll stays cheap even when WPF falls back to software rasterization:
/// the element itself draws only the gridlines, a BitmapCache'd child visual holds
/// the series and is re-rasterized only when data or the y-scale changes (~1 Hz),
/// and per frame the scroll just translates that cached bitmap (four instances are
/// live at once, so this matters). The scale readout sits on a tiny overlay visual
/// above the series, refreshed only when its text changes.
///
/// The right edge displays time now-<see cref="DisplayDelay"/>, so each new 1 Hz
/// sample is rasterized just past the visible edge and slides in with the scroll
/// instead of popping into a gap. The translation stays fractional on purpose —
/// bilinear sub-pixel motion is what makes the slow scroll read as continuous.
/// </summary>
public sealed class MetricGraph : FrameworkElement
{
    private readonly struct Pt
    {
        public readonly double TimeSec, Value;
        public Pt(double t, double v) { TimeSec = t; Value = v; }
    }

    /// <summary>DrawingVisual with its protected cache mode exposed.</summary>
    private sealed class Layer : DrawingVisual
    {
        public void SetCacheMode(CacheMode? mode) => VisualCacheMode = mode;
    }

    private const double PadTop = 6, PadBottom = 4;

    /// <summary>Length of the displayed window in seconds ("Last 5 minutes").</summary>
    public const double WindowSeconds = 300;

    /// <summary>Seconds the live view trails real time; must exceed the sample
    /// interval (1 s) plus delivery jitter so the right edge never runs dry.</summary>
    public const double DisplayDelay = 1.75;

    private readonly List<Pt> _points = new();
    private bool _bytes;
    private double _floor = 10;       // the scale never drops below this
    private double _renderPeak = 10;  // eased scale the raster was drawn at

    private readonly Layer _series = new();          // area + line; scrolled via _scroll
    private readonly DrawingVisual _overlay = new(); // scale readout, above the series
    private readonly TranslateTransform _scroll = new();
    private double _anchorSec;                       // time mapped to the right edge when _series was rendered
    private bool _seriesDirty = true;
    private string _readoutText = "";

    // Readout label cache: FormattedText is expensive, rebuilt only when the text changes.
    private string? _labelKey;
    private FormattedText? _labelFt;
    private double _dpi = 1.0;

    private DateTime _lastFrame = DateTime.MinValue;
    private DateTime _lastShrink = DateTime.MinValue;

    private readonly Brush _fill;
    private readonly Pen _line;
    private readonly Pen _grid;
    private readonly Brush _text;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    private static Color ResColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    public MetricGraph()
    {
        ClipToBounds = true; // the cached series is rasterized past the right edge and shifted in

        var accent = ResColor("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, accent.R, accent.G, accent.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x0E, accent.R, accent.G, accent.B), 1));
        b.Freeze();
        _fill = b;
        _line = new Pen(new SolidColorBrush(accent), 1.4); _line.Freeze();
        _grid = new Pen(new SolidColorBrush(ResColor("GridLineColor", Color.FromRgb(0xE4, 0xE7, 0xEB))), 1); _grid.Freeze();
        _text = new SolidColorBrush(ResColor("GridTextColor", Color.FromRgb(0x79, 0x81, 0x8B))); _text.Freeze();

        _series.Transform = _scroll;
        AddVisualChild(_series);
        AddVisualChild(_overlay);

        Loaded += (_, _) =>
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (dpi != _dpi)
            {
                // The first layout pass ran before Loaded, with _dpi still 1.0 —
                // a label baked then used the wrong pixelsPerDip.
                _dpi = dpi;
                _labelKey = null;
                _labelFt = null;
                RefreshReadout(force: true);
            }
            _series.SetCacheMode(new BitmapCache { RenderAtScale = _dpi });
            CompositionTarget.Rendering -= OnFrame; // re-load must not double-subscribe
            CompositionTarget.Rendering += OnFrame;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _series,
        1 => _overlay,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.PixelsPerDip;
        _series.SetCacheMode(new BitmapCache { RenderAtScale = _dpi });
        _labelKey = null;
        _labelFt = null;
        _seriesDirty = true; // the next live frame re-rasterizes at the new scale
        RefreshReadout(force: true);
        base.OnDpiChanged(oldDpi, newDpi);
    }

    /// <summary>
    /// Push the rolling history as parallel timestamp/value lists (epoch seconds).
    /// Called ~1 Hz with the full service buffer; the raster is rebuilt on the next
    /// live frame. Points older than the displayed window (plus a small margin) are
    /// dropped so a buffer spanning hours (e.g. across sleep) stays cheap.
    /// </summary>
    public void SetSamples(IReadOnlyList<double> timesEpochSec, IReadOnlyList<double> values, bool bytes, double? unitScale = null)
    {
        _bytes = bytes;
        _floor = unitScale ?? (bytes ? 1_000_000 : 10);
        _points.Clear();
        int n = Math.Min(timesEpochSec.Count, values.Count);
        double cut = n > 0 ? timesEpochSec[n - 1] - WindowSeconds - DisplayDelay - 10 : 0;
        for (int i = 0; i < n; i++)
            if (timesEpochSec[i] >= cut)
                _points.Add(new Pt(timesEpochSec[i], values[i]));
        _seriesDirty = true; // picked up by the next live frame
    }

    /// <summary>Set the series without timestamps (compatibility overload): samples
    /// are assumed to be 1 Hz ending now, then scroll like a live feed.</summary>
    public void SetValues(IReadOnlyList<double> values, bool bytes, double? unitScale = null)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var times = new double[values.Count];
        for (int i = 0; i < times.Length; i++) times[i] = now - (times.Length - 1 - i);
        SetSamples(times, values, bytes, unitScale);
    }

    private string Fmt(double v) => _bytes ? ByteFormatter.Rate(v) : $"{v:0} %";

    private void OnFrame(object? sender, EventArgs e)
    {
        // Hidden pages stay Loaded, so gate on visibility. Four instances share this
        // event — per-frame work must stay a cheap translate and nothing else.
        if (!IsVisible) return;
        var now = DateTime.UtcNow;
        // The 5-min window scrolls well under a pixel per frame — 20 fps is plenty.
        if ((now - _lastFrame).TotalMilliseconds < 50) return;
        _lastFrame = now;

        double edge = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - DisplayDelay;
        if (UpdatePeak(edge - WindowSeconds)) _seriesDirty = true;

        if (_seriesDirty)
            RenderSeries(_points.Count > 0 ? _points[^1].TimeSec : edge);

        // Positive X parks the not-yet-visible newest sample past the right edge;
        // it slides in as the edge advances. Fractional offsets are intentional.
        _scroll.X = (_anchorSec - edge) / WindowSeconds * ActualWidth;

        RefreshReadout();
    }

    /// <summary>
    /// Retarget the render scale to the peak of the visible window (including the
    /// just-arrived samples still past the edge); true if it moved. Grows instantly
    /// (a spike must never draw clipped), shrinks in gentle steps throttled to
    /// ~5 Hz because every step re-rasterizes the cached series.
    /// </summary>
    private bool UpdatePeak(double fromSec)
    {
        double target = 0;
        for (int i = FirstAtOrAfter(fromSec); i < _points.Count; i++)
            target = Math.Max(target, _points[i].Value);
        target = Math.Max(target * 1.15, _floor);
        if (target > _renderPeak) { _renderPeak = target; return true; }
        if (Math.Abs(target - _renderPeak) < _renderPeak * 0.002) return false;
        // Each shrink step re-rasterizes the cached series; four graphs share the
        // UI thread with the scroll, so throttle the (non-urgent) downscale to
        // ~3 Hz to keep re-raster bursts from stealing scroll frames.
        if ((DateTime.UtcNow - _lastShrink).TotalMilliseconds < 320) return false;
        _lastShrink = DateTime.UtcNow;
        _renderPeak += (target - _renderPeak) * 0.34;
        if (Math.Abs(target - _renderPeak) < _renderPeak * 0.002) _renderPeak = target;
        return true;
    }

    /// <summary>Re-rasterize the cached series layer anchored at <paramref name="anchorSec"/>.</summary>
    private void RenderSeries(double anchorSec)
    {
        _anchorSec = anchorSec;
        _scroll.X = 0;
        _seriesDirty = false;

        using var dc = _series.RenderOpen();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2 || _points.Count < 2) return;

        double fromSec = anchorSec - WindowSeconds;
        double plotH = h - PadTop - PadBottom;
        double peak = Math.Max(1e-6, _renderPeak);
        double X(double t) => (t - fromSec) / WindowSeconds * w;
        double Y(double v) => PadTop + plotH - Math.Min(1.0, v / peak) * plotH;

        // The raster trails the anchor by up to DisplayDelay while it scrolls, so
        // cover that much extra history left of the window.
        int start = FirstAtOrAfter(fromSec - DisplayDelay - 5);
        if (_points.Count - start < 2) return;

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(_points[start].TimeSec), h - PadBottom), true, true);
            ctx.LineTo(new Point(X(_points[start].TimeSec), Y(_points[start].Value)), true, false);
            for (int i = start + 1; i < _points.Count; i++)
                ctx.LineTo(new Point(X(_points[i].TimeSec), Y(_points[i].Value)), true, true);
            ctx.LineTo(new Point(X(_points[^1].TimeSec), h - PadBottom), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(_fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(_points[start].TimeSec), Y(_points[start].Value)), false, false);
            for (int i = start + 1; i < _points.Count; i++)
                ctx.LineTo(new Point(X(_points[i].TimeSec), Y(_points[i].Value)), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, _line, line);
    }

    /// <summary>Refresh the scale readout when its text changes (it tracks the drawn scale).</summary>
    private void RefreshReadout(bool force = false)
    {
        string readout = Fmt(Math.Max(_floor, _renderPeak));
        if (!force && readout == _readoutText) return;
        _readoutText = readout;
        RenderOverlay();
    }

    /// <summary>Draw the scale readout on the overlay visual (right-aligned, above the series).</summary>
    private void RenderOverlay()
    {
        using var dc = _overlay.RenderOpen();
        double w = ActualWidth;
        if (w <= 2 || _readoutText.Length == 0) return;
        var ft = CachedLabel(_readoutText, 11);
        dc.DrawText(ft, new Point(w - ft.Width - 4, 1));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderSeries(_points.Count > 0
            ? _points[^1].TimeSec
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - DisplayDelay);
        RenderOverlay(); // the readout is right-aligned, so it moves with the width
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        // top + mid gridlines (static; the series and readout live on child visuals)
        double plotH = h - PadTop - PadBottom;
        dc.DrawLine(_grid, new Point(0, PadTop), new Point(w, PadTop));
        dc.DrawLine(_grid, new Point(0, PadTop + plotH / 2), new Point(w, PadTop + plotH / 2));
    }

    /// <summary>Index of the first point at or after <paramref name="cutSec"/> (points are time-ordered).</summary>
    private int FirstAtOrAfter(double cutSec)
    {
        int lo = 0, hi = _points.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_points[mid].TimeSec < cutSec) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private FormattedText CachedLabel(string text, double size)
    {
        if (_labelFt is null || _labelKey != text)
        {
            _labelFt = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _mono, size, _text, _dpi);
            _labelKey = text;
        }
        return _labelFt;
    }
}
