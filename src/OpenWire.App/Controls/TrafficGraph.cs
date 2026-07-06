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
/// <see cref="TrafficSeries"/> for historical ranges. A timeline strip can zoom
/// the view into a sub-window via <see cref="ZoomTo"/>/<see cref="SetLiveWindow"/>.
///
/// Rendering is split into three layers so the live scroll stays cheap even when
/// WPF falls back to software rasterization (virtual display adapters / remote
/// streaming): the element itself draws only gridlines + labels (redrawn when the
/// scale text changes), a BitmapCache'd child visual holds the series and is
/// re-rasterized only when data or the y-scale changes (~1 Hz), and per frame the
/// scroll just translates that cached bitmap. The hover inspect card lives on a
/// third tiny overlay visual so mouse moves never re-rasterize the chart.
///
/// The live right edge displays time now-<see cref="DisplayDelay"/>, so the curve
/// is always complete up to the edge: each new sample is rasterized just beyond
/// the visible window and slides in with the scroll, instead of popping into a
/// growing gap once a second. The translation stays fractional on purpose —
/// bilinear sub-pixel motion is what makes the slow scroll read as continuous.
/// </summary>
public sealed class TrafficGraph : FrameworkElement
{
    private readonly struct Pt
    {
        public readonly double TimeSec, In, Out;
        public Pt(double t, double i, double o) { TimeSec = t; In = i; Out = o; }
    }

    /// <summary>A marker dropped on the timeline (e.g. a new app's first connection).</summary>
    private readonly struct Pin
    {
        public readonly double TimeSec;
        public readonly string Label;
        public Pin(double t, string label) { TimeSec = t; Label = label; }
    }

    /// <summary>DrawingVisual with its protected cache mode exposed.</summary>
    private sealed class Layer : DrawingVisual
    {
        public void SetCacheMode(CacheMode? mode) => VisualCacheMode = mode;
    }

    private const double PadTop = 14, PadBottom = 6;

    /// <summary>Seconds the live view trails real time; must exceed the sample
    /// interval (1 s) plus delivery jitter so the right edge never runs dry.</summary>
    private const double DisplayDelay = 1.75;

    private readonly List<Pt> _points = new();
    private readonly List<Pin> _pins = new();
    private double _peak = 8 * 1024;
    private double _renderPeak = 8 * 1024;
    private bool _volumeUnits;
    private double _intervalSec = 1.0; // seconds one sample covers (bytes = rate * interval)
    private double? _hoverX;

    // Horizontal drag-selection on the plot (GlassWire-style): stats for the band.
    private double? _selFrom, _selTo;
    private bool _selDragging, _selMoved;
    private double _selDownX;

    private readonly Layer _series = new();          // areas + lines + pins; scrolled via _scroll
    private readonly DrawingVisual _overlay = new(); // scale readout + hover card
    private readonly TranslateTransform _scroll = new();
    private readonly ScaleTransform _zoomScale = new(); // drag-preview stretch of the cached raster
    private double _anchorSec;                       // time mapped to the right edge when _series was rendered
    private double _rasterWindowSec = 300;           // window length the raster was drawn at
    private DateTime _lastZoomRaster = DateTime.MinValue;
    private bool _seriesDirty = true;
    private string _readoutText = "";

    private bool _rangeLive = true;      // loaded range is the rolling 5-min buffer
    private double _rangeSeconds = 300;  // full extent of the loaded range (live: buffer length)
    private bool _zoomed;                // timeline strip selected a frozen sub-window
    private double _viewFrom, _viewTo;   // the sub-window when _zoomed

    private readonly Brush _inFill, _outFill;
    private readonly Pen _inPen, _outPen, _gridPen;
    private readonly Brush _gridText;
    private readonly Brush _idleFill;
    private readonly Pen _pinLine;
    private readonly Brush _pinFill;
    private readonly Pen _hoverPen;
    private readonly Brush _hoverCardBg;
    private readonly Pen _hoverCardPen;
    private readonly Brush _selFill;
    private readonly Pen _selEdge;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    // Scale-label cache: slot 0 = the upper-left readout, 1..4 = gridline labels.
    // FormattedText is expensive; each slot is rebuilt only when its text changes.
    private readonly string?[] _labelKey = new string?[5];
    private readonly FormattedText?[] _labelFt = new FormattedText?[5];
    private double _dpi = 1.0;

    private DateTime _lastFrame = DateTime.MinValue;
    private DateTime _lastShrink = DateTime.MinValue;

    /// <summary>Length of the *displayed* window (shorter than the range when zoomed).</summary>
    public double WindowSeconds { get; private set; } = 300;

    /// <summary>The view is following the live edge (live range, no frozen zoom).</summary>
    public bool LiveScroll => _rangeLive && !_zoomed;

    /// <summary>The loaded range is the rolling live buffer.</summary>
    public bool IsLiveRange => _rangeLive;

    /// <summary>Freeze the live scroll (data keeps buffering; the view stops advancing).</summary>
    public bool Paused { get; set; }

    /// <summary>A band was drag-selected on the plot: (fromSec, toSec, downBytes, upBytes).</summary>
    public event Action<double, double, double, double>? RangeSelected;

    /// <summary>The drag-selection was dismissed (click, or a new series arrived).</summary>
    public event Action? SelectionCleared;

    /// <summary>Raised when the visible time window changes (range switch, zoom, live advance),
    /// so the host can label the graph's current span. Args are (fromSec, toSec) unix seconds.</summary>
    public event Action<double, double>? ViewChanged;

    /// <summary>The time span currently drawn: [from, to] in unix seconds.</summary>
    public (double From, double To) VisibleWindow()
    {
        double to = _zoomed ? _viewTo
            : LiveScroll ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - DisplayDelay
            : _anchorSec;
        return (to - WindowSeconds, to);
    }

    private void NotifyView() { var (f, t) = VisibleWindow(); ViewChanged?.Invoke(f, t); }

    public TrafficGraph()
    {
        ClipToBounds = true; // the cached series is rasterized past the right edge and shifted in

        Color inC = ResColor("InFillColor", Color.FromRgb(0x2F, 0xB8, 0xC6));
        Color inL = ResColor("InLineColor", Color.FromRgb(0x5F, 0xE3, 0xEF));
        Color outC = ResColor("OutFillColor", Color.FromRgb(0xE8, 0xA1, 0x3A));
        Color outL = ResColor("OutLineColor", Color.FromRgb(0xFF, 0xC4, 0x6B));

        _inFill = VerticalFade(inC, 0.72, 0.10);
        _outFill = VerticalFade(outC, 0.68, 0.08);
        _inPen = new Pen(new SolidColorBrush(inL), 1.4); _inPen.Freeze();
        _outPen = new Pen(new SolidColorBrush(outL), 1.4); _outPen.Freeze();
        _gridPen = new Pen(new SolidColorBrush(ResColor("GridLineColor", Color.FromRgb(0xE4, 0xE7, 0xEB))), 1); _gridPen.Freeze();
        _gridText = new SolidColorBrush(ResColor("GridTextColor", Color.FromRgb(0x79, 0x81, 0x8B))); _gridText.Freeze();
        _idleFill = new SolidColorBrush(Color.FromArgb(0x16, 0x94, 0x9E, 0xAD)); _idleFill.Freeze();

        var accent = ResColor("AccentColor", Color.FromRgb(0x3B, 0x82, 0xF6));
        _pinLine = new Pen(new SolidColorBrush(Color.FromArgb(0x4A, accent.R, accent.G, accent.B)), 1)
            { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        _pinLine.Freeze();
        _pinFill = new SolidColorBrush(accent); _pinFill.Freeze();

        _selFill = new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B)); _selFill.Freeze();
        _selEdge = new Pen(new SolidColorBrush(Color.FromArgb(0x7A, accent.R, accent.G, accent.B)), 1); _selEdge.Freeze();

        var ts = ResColor("TextSecondaryColor", Color.FromRgb(0x5B, 0x64, 0x72));
        _hoverPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, ts.R, ts.G, ts.B)), 1); _hoverPen.Freeze();
        _hoverCardBg = new SolidColorBrush(ResColor("BgPanelColor", Colors.White)) { Opacity = 0.97 }; _hoverCardBg.Freeze();
        _hoverCardPen = new Pen(new SolidColorBrush(ResColor("BorderStrongColor", Color.FromRgb(0xD3, 0xD8, 0xE0))), 1); _hoverCardPen.Freeze();

        var tg = new TransformGroup();
        tg.Children.Add(_zoomScale); // scale first, then translate: x' = x * sx + tx
        tg.Children.Add(_scroll);
        _series.Transform = tg;
        AddVisualChild(_series);
        AddVisualChild(_overlay);

        Loaded += (_, _) =>
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (dpi != _dpi)
            {
                // The first layout pass ran before Loaded, with _dpi still 1.0 —
                // any labels baked then used the wrong pixelsPerDip.
                _dpi = dpi;
                Array.Clear(_labelKey);
                Array.Clear(_labelFt);
                RefreshStatic(force: true);
            }
            _series.SetCacheMode(new BitmapCache { RenderAtScale = _dpi });
            CompositionTarget.Rendering -= OnFrame; // re-load must not double-subscribe
            CompositionTarget.Rendering += OnFrame;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;

        MouseMove += (_, e) =>
        {
            double x = e.GetPosition(this).X;
            _hoverX = x;
            if (_selDragging)
            {
                if (!_selMoved && Math.Abs(x - _selDownX) > 4) _selMoved = true;
                if (_selMoved)
                {
                    _selFrom = TimeAtX(Math.Min(_selDownX, x));
                    _selTo = TimeAtX(Math.Max(_selDownX, x));
                    RenderOverlay();
                    return;
                }
            }
            if (!LiveScroll) RenderOverlay();
        };
        MouseLeave += (_, _) => { _hoverX = null; if (!LiveScroll) RenderOverlay(); };
        MouseLeftButtonDown += (_, e) =>
        {
            _selDragging = true;
            _selMoved = false;
            _selDownX = e.GetPosition(this).X;
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_selDragging) return;
            _selDragging = false;
            ReleaseMouseCapture();
            if (_selMoved && _selFrom is double f && _selTo is double t && t - f >= 2) CommitSelection(f, t);
            else ClearSelection(notify: true); // a plain click dismisses the band
        };
    }

    /// <summary>Time currently mapped to the right edge (valid in every state, incl. paused).</summary>
    private double DisplayedEdgeSec() => _anchorSec - _scroll.X * WindowSeconds / Math.Max(1.0, ActualWidth);

    private double TimeAtX(double x) => DisplayedEdgeSec() + (x / Math.Max(1.0, ActualWidth) - 1.0) * WindowSeconds;

    private void ClearSelection(bool notify)
    {
        bool had = _selFrom is not null;
        _selFrom = _selTo = null;
        RenderOverlay();
        if (had && notify) SelectionCleared?.Invoke();
    }

    /// <summary>Sum the selected band and announce it (down/up in bytes).</summary>
    private void CommitSelection(double fromSec, double toSec)
    {
        double dn = 0, up = 0;
        double k = _volumeUnits ? 1.0 : _intervalSec; // rate points cover one interval each
        for (int i = FirstAtOrAfter(fromSec); i < _points.Count && _points[i].TimeSec <= toSec; i++)
        {
            dn += _points[i].In * k;
            up += _points[i].Out * k;
        }
        RenderOverlay();
        RangeSelected?.Invoke(fromSec, toSec, dn, up);
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
        Array.Clear(_labelKey);
        Array.Clear(_labelFt);
        // A DPI change may not change the DIU size, so nothing else re-renders for us:
        // force the labels/overlay now, and re-anchor the series if no live frame will.
        _seriesDirty = true;
        if (!LiveScroll) RenderSeries(_anchorSec);
        RefreshStatic(force: true);
        base.OnDpiChanged(oldDpi, newDpi);
    }

    /// <summary>Drop a labelled marker on the timeline (e.g. a new app's first connection).</summary>
    public void AddPin(double epochSec, string label)
    {
        _pins.Add(new Pin(epochSec, label));
        if (_pins.Count > 200) _pins.RemoveAt(0);
        _seriesDirty = true;
        if (!LiveScroll) RenderSeries(_anchorSec);
    }

    public void ClearPins()
    {
        _pins.Clear();
        _seriesDirty = true;
        if (!LiveScroll) RenderSeries(_anchorSec);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Collapsed sub-views stay Loaded, so also gate on visibility.
        if (!LiveScroll || Paused || !IsVisible) return;
        var now = DateTime.UtcNow;
        // The 5-min window scrolls ~7 px/s — 20 fps is plenty; zoomed-in live
        // windows scroll faster and get ~40 fps to stay fluid.
        double frameMs = WindowSeconds <= 90 ? 25 : 50;
        if ((now - _lastFrame).TotalMilliseconds < frameMs) return;
        _lastFrame = now;

        double nowSec = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double edge = nowSec - DisplayDelay; // time shown at the right edge
        if (UpdatePeak(edge - WindowSeconds, double.MaxValue)) _seriesDirty = true;

        if (_seriesDirty)
            RenderSeries(_points.Count > 0 ? _points[^1].TimeSec : edge);

        // Positive X parks the not-yet-visible newest samples past the right edge;
        // they slide in as the edge advances. Fractional offsets are intentional.
        _scroll.X = (_anchorSec - edge) / WindowSeconds * ActualWidth;

        RefreshStatic();
        if (_selFrom is not null) RenderOverlay(); // the band is time-anchored: keep it gliding with the scroll
    }

    /// <summary>Push a single live sample (bytes in the last second == bytes/sec).</summary>
    public void AddSample(double epochSec, double inBytes, double outBytes)
    {
        // Ticks can still arrive while a historical range is displayed (the async
        // fetch after a range switch); the eventual SetSeries backfills these seconds.
        if (!_rangeLive) return;
        _points.Add(new Pt(epochSec, inBytes, outBytes));
        // Frozen views (paused / zoomed) must keep their window's data alive.
        double keep = epochSec - _rangeSeconds - 5;
        if (Paused) keep = Math.Min(keep, _anchorSec - _rangeSeconds - 5);
        if (_zoomed) keep = Math.Min(keep, _viewFrom - 5);
        if (_selFrom is double sf) keep = Math.Min(keep, sf - 5); // selection stats stay valid
        while (_points.Count > 0 && _points[0].TimeSec < keep) _points.RemoveAt(0);
        _seriesDirty = true; // picked up by the next live frame
        NotifyView();        // live edge advanced → refresh the range label
    }

    /// <summary>Load a full historical series (switches to static layout for long ranges).</summary>
    public void SetSeries(TrafficSeries series)
    {
        ClearSelection(notify: true); // stats belong to the old series
        _points.Clear();
        bool live = series.Range is GraphRange.FiveMinutes;
        _rangeLive = live;
        _zoomed = false;
        _rangeSeconds = WindowSeconds = series.Range.Duration().TotalSeconds;
        _volumeUnits = series.Range is not (GraphRange.FiveMinutes or GraphRange.ThreeHours);
        _intervalSec = live ? 1.0 : Math.Max(1.0, series.IntervalSeconds);

        double div = live ? 1.0 : series.IntervalSeconds; // per-interval bytes -> rate unless volume
        foreach (var s in series.Samples)
        {
            double t = s.Time.ToUnixTimeSeconds();
            double scale = _volumeUnits ? 1.0 : 1.0 / div;
            _points.Add(new Pt(t, s.BytesIn * scale, s.BytesOut * scale));
        }

        double anchor = _points.Count > 0
            ? _points[^1].TimeSec
            : (live ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - DisplayDelay : 0);
        _anchorSec = anchor;
        UpdatePeak(anchor - WindowSeconds, double.MaxValue);
        _renderPeak = _peak; // new range, new scale: easing from the old range's peak is meaningless
        RenderSeries(anchor);
        RefreshStatic(force: true);
        NotifyView();
    }

    /// <summary>
    /// Per-mouse-move zoom/pan preview while a strip handle is being dragged: the
    /// view tracks the mouse at frame rate by stretching/translating the cached
    /// raster (near-free), and re-rasterizes crisp at ~5 Hz underneath. The final
    /// <see cref="ZoomTo"/>/<see cref="ResetZoom"/> on release paints the exact view.
    /// </summary>
    public void PreviewWindow(double fromSec, double toSec)
    {
        if (_points.Count < 2) return;
        fromSec = Math.Max(fromSec, _points[0].TimeSec);
        toSec = Math.Min(toSec, _points[^1].TimeSec);
        if (toSec - fromSec < 10) return;
        _zoomed = true;
        _viewFrom = fromSec;
        _viewTo = toSec;
        WindowSeconds = toSec - fromSec;

        if ((DateTime.UtcNow - _lastZoomRaster).TotalMilliseconds >= 200)
        {
            _lastZoomRaster = DateTime.UtcNow;
            UpdatePeak(fromSec, toSec);
            _renderPeak = _peak;
            RenderSeries(toSec);
            RefreshStatic(force: true);
            return;
        }

        double w = ActualWidth;
        if (w <= 1 || _rasterWindowSec <= 0) return;
        _zoomScale.ScaleX = _rasterWindowSec / WindowSeconds;
        _scroll.X = (_anchorSec - _rasterWindowSec - fromSec) / WindowSeconds * w;
    }

    /// <summary>Freeze the view onto a sub-window of the loaded range (timeline strip drag).</summary>
    public void ZoomTo(double fromSec, double toSec)
    {
        if (_points.Count < 2) return;
        fromSec = Math.Max(fromSec, _points[0].TimeSec);
        toSec = Math.Min(toSec, _points[^1].TimeSec);
        if (toSec - fromSec < 10) return;
        _zoomed = true;
        _viewFrom = fromSec;
        _viewTo = toSec;
        WindowSeconds = toSec - fromSec;
        UpdatePeak(fromSec, toSec);
        _renderPeak = _peak;
        RenderSeries(toSec);
        RefreshStatic(force: true);
        NotifyView();
    }

    /// <summary>Narrow the live window (strip selection pinned to the right edge) — keeps scrolling.</summary>
    public void SetLiveWindow(double seconds)
    {
        if (!_rangeLive) return;
        _zoomed = false;
        WindowSeconds = Math.Max(30, Math.Min(seconds, _rangeSeconds));
        _seriesDirty = true;
        _lastFrame = DateTime.MinValue; // re-render on the very next frame
        NotifyView();
    }

    /// <summary>Back to the full range (live scroll resumes on the live range).</summary>
    public void ResetZoom()
    {
        if (!_zoomed && WindowSeconds == _rangeSeconds) return;
        _zoomed = false;
        WindowSeconds = _rangeSeconds;
        if (_rangeLive)
        {
            _seriesDirty = true;
            _lastFrame = DateTime.MinValue;
            NotifyView();
            return;
        }
        double anchor = _points.Count > 0 ? _points[^1].TimeSec : _anchorSec;
        UpdatePeak(anchor - WindowSeconds, anchor);
        _renderPeak = _peak;
        RenderSeries(anchor);
        RefreshStatic(force: true);
        NotifyView();
    }

    public void Clear()
    {
        _points.Clear();
        _seriesDirty = true;
        if (!LiveScroll) RenderSeries(_anchorSec);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // While paused, keep the frozen anchor — re-anchoring to now would jump the view forward.
        double anchor = LiveScroll && !Paused
            ? (_points.Count > 0 ? _points[^1].TimeSec : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - DisplayDelay)
            : _anchorSec;
        RenderSeries(anchor);
        RefreshStatic(force: true);
    }

    /// <summary>
    /// Retarget the render scale to the peak of [fromSec, toSec]; true if it moved.
    /// Grows instantly (a spike must never draw clipped), shrinks in gentle steps
    /// throttled to ~5 Hz because every step re-rasterizes the cached series.
    /// </summary>
    private bool UpdatePeak(double fromSec, double toSec)
    {
        double target = 8 * 1024;
        for (int i = FirstAtOrAfter(fromSec); i < _points.Count && _points[i].TimeSec <= toSec; i++)
        {
            var p = _points[i];
            target = Math.Max(target, Math.Max(p.In, p.Out));
        }
        target *= 1.18;
        _peak = target;
        if (target > _renderPeak) { _renderPeak = target; return true; }
        if (Math.Abs(target - _renderPeak) < _renderPeak * 0.002) return false;
        if ((DateTime.UtcNow - _lastShrink).TotalMilliseconds < 200) return false;
        _lastShrink = DateTime.UtcNow;
        _renderPeak += (target - _renderPeak) * 0.25;
        if (Math.Abs(target - _renderPeak) < _renderPeak * 0.002) _renderPeak = target;
        return true;
    }

    /// <summary>Re-rasterize the cached series layer anchored at <paramref name="anchorSec"/>.</summary>
    private void RenderSeries(double anchorSec)
    {
        _anchorSec = anchorSec;
        _rasterWindowSec = WindowSeconds;
        _scroll.X = 0;
        _zoomScale.ScaleX = 1;
        _seriesDirty = false;

        using var dc = _series.RenderOpen();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1 || _points.Count < 2) return;

        double fromSec = anchorSec - WindowSeconds;
        double plotH = h - PadTop - PadBottom;
        double peak = Math.Max(1024, _renderPeak);
        double X(double t) => (t - fromSec) / WindowSeconds * w;
        double Y(double v) => PadTop + plotH - Math.Min(1.0, v / peak) * plotH;
        // Live rasters trail the anchor by DisplayDelay, so cover that much extra
        // history; zoomed rasters get 60% spare width per side so drag previews
        // (which pan/stretch this raster) don't run off the painted region.
        double lead = _zoomed ? WindowSeconds * 0.6 : (LiveScroll ? DisplayDelay : 0);
        int start = FirstAtOrAfter(fromSec - lead - 5);
        int end = _zoomed ? FirstAtOrAfter(_viewTo + WindowSeconds * 0.6) : _points.Count;

        // Idle shading: on historical ranges, wash the spans where nothing was
        // recorded (PC asleep / no activity) — GlassWire's grey "no-data" bands.
        if (!LiveScroll) DrawIdleBands(dc, X, PadTop, plotH, start, end, peak);

        DrawArea(dc, X, Y, h - PadBottom, p => p.Out, _outFill, _outPen, start, end);
        DrawArea(dc, X, Y, h - PadBottom, p => p.In, _inFill, _inPen, start, end);
        DrawPins(dc, X, PadTop, h - PadBottom);
    }

    /// <summary>Refresh the readout overlay and gridline labels when the scale text changes.</summary>
    private void RefreshStatic(bool force = false)
    {
        double peak = Math.Max(1024, _renderPeak);
        string readout = _volumeUnits ? ByteFormatter.Bytes((long)peak) : ByteFormatter.Rate(peak);
        if (!force && readout == _readoutText) return;
        _readoutText = readout;
        RenderOverlay();
        InvalidateVisual(); // gridline labels share the scale
    }

    /// <summary>Draw the readout + (historical) hover card on the overlay visual.</summary>
    private void RenderOverlay()
    {
        using var dc = _overlay.RenderOpen();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        DrawSelection(dc, w, h);

        if (_readoutText.Length > 0)
            dc.DrawText(CachedLabel(0, _readoutText, 12), new Point(8, 6));

        DrawHover(dc, w, h);
    }

    /// <summary>Tint the drag-selected time band (drawn in current view coordinates).</summary>
    private void DrawSelection(DrawingContext dc, double w, double h)
    {
        if (_selFrom is not double f || _selTo is not double t) return;
        double from = DisplayedEdgeSec() - WindowSeconds;
        double x0 = Math.Max(0, (f - from) / WindowSeconds * w);
        double x1 = Math.Min(w, (t - from) / WindowSeconds * w);
        if (x1 <= x0) return;
        double plotH = h - PadTop - PadBottom;
        dc.DrawRectangle(_selFill, null, new Rect(x0, PadTop, x1 - x0, plotH));
        dc.DrawLine(_selEdge, new Point(x0, PadTop), new Point(x0, PadTop + plotH));
        dc.DrawLine(_selEdge, new Point(x1, PadTop), new Point(x1, PadTop + plotH));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // make the plot hit-testable for hover

        double plotH = h - PadTop - PadBottom;
        double peak = Math.Max(1024, _renderPeak);
        for (int i = 1; i <= 4; i++)
        {
            double frac = i / 4.0;
            double y = PadTop + plotH - frac * plotH;
            dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            double val = peak * frac;
            string s = _volumeUnits ? ByteFormatter.Bytes((long)val) : ByteFormatter.Rate(val);
            var ft = CachedLabel(i, s, 10);
            dc.DrawText(ft, new Point(w - ft.Width - 6, y + 1));
        }
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

    private FormattedText CachedLabel(int slot, string text, double size)
    {
        if (_labelFt[slot] is null || _labelKey[slot] != text)
        {
            _labelFt[slot] = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _mono, size, _gridText, _dpi);
            _labelKey[slot] = text;
        }
        return _labelFt[slot]!;
    }

    /// <summary>Draw the new-app event markers along the timeline.</summary>
    private void DrawPins(DrawingContext dc, Func<double, double> X, double top, double bottom)
    {
        if (_pins.Count == 0) return;
        foreach (var p in _pins)
        {
            double x = X(p.TimeSec);
            if (x < 0 || x > ActualWidth) continue;
            dc.DrawLine(_pinLine, new Point(x, top), new Point(x, bottom));
            var flag = new StreamGeometry();
            using (var c = flag.Open())
            {
                c.BeginFigure(new Point(x, top - 1), true, true);
                c.LineTo(new Point(x + 9, top + 2), true, true);
                c.LineTo(new Point(x, top + 5), true, true);
            }
            flag.Freeze();
            dc.DrawGeometry(_pinFill, null, flag);
        }
    }

    /// <summary>On frozen views, draw a crosshair + inspect card at the hovered interval.</summary>
    private void DrawHover(DrawingContext dc, double w, double h)
    {
        if (LiveScroll || _hoverX is not double mx || _points.Count == 0) return;

        double plotH = h - PadTop - PadBottom;
        double peak = Math.Max(1024, _renderPeak);
        double fromSec = _anchorSec - WindowSeconds;
        double X(double t) => (t - fromSec) / WindowSeconds * w;

        double tHover = fromSec + mx / w * WindowSeconds;
        Pt best = _points[0]; double bd = double.MaxValue;
        foreach (var p in _points) { double d = Math.Abs(p.TimeSec - tHover); if (d < bd) { bd = d; best = p; } }
        double x = X(best.TimeSec);
        if (x < 0 || x > w) return;

        dc.DrawLine(_hoverPen, new Point(x, PadTop), new Point(x, PadTop + plotH));
        double yIn = PadTop + plotH - Math.Min(1.0, best.In / peak) * plotH;
        double yOut = PadTop + plotH - Math.Min(1.0, best.Out / peak) * plotH;
        dc.DrawEllipse(_inPen.Brush, null, new Point(x, yIn), 3, 3);
        dc.DrawEllipse(_outPen.Brush, null, new Point(x, yOut), 3, 3);

        string time = DateTimeOffset.FromUnixTimeSeconds((long)best.TimeSec).ToLocalTime()
            .ToString(_volumeUnits ? "MMM d  HH:mm" : "HH:mm:ss");
        string dn = "↓ " + (_volumeUnits ? ByteFormatter.Bytes((long)best.In) : ByteFormatter.Rate(best.In));
        string up = "↑ " + (_volumeUnits ? ByteFormatter.Bytes((long)best.Out) : ByteFormatter.Rate(best.Out));

        FormattedText Ft(string s, Brush b, double sz) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, sz, b, _dpi);
        var l0 = Ft(time, _gridText, 11);
        var l1 = Ft(dn, _inPen.Brush, 12);
        var l2 = Ft(up, _outPen.Brush, 12);

        double cardW = Math.Max(l0.Width, Math.Max(l1.Width, l2.Width)) + 20;
        double cardH = l0.Height + l1.Height + l2.Height + 16;
        double cx = x + 12; if (cx + cardW > w) cx = x - cardW - 12; if (cx < 2) cx = 2;
        double cy = Math.Max(2, PadTop + 4);

        dc.DrawRoundedRectangle(_hoverCardBg, _hoverCardPen, new Rect(cx, cy, cardW, cardH), 6, 6);
        dc.DrawText(l0, new Point(cx + 10, cy + 6));
        dc.DrawText(l1, new Point(cx + 10, cy + 6 + l0.Height));
        dc.DrawText(l2, new Point(cx + 10, cy + 6 + l0.Height + l1.Height));
    }

    /// <summary>Shade contiguous idle spans (combined throughput below a small floor).</summary>
    private void DrawIdleBands(DrawingContext dc, Func<double, double> X, double top, double plotH, int start, int end, double peak)
    {
        if (end - start < 2) return;

        double floor = Math.Max(64, peak * 0.02);
        int i = start;
        while (i < end)
        {
            if (_points[i].In + _points[i].Out <= floor)
            {
                int j = i;
                while (j + 1 < end && _points[j + 1].In + _points[j + 1].Out <= floor) j++;
                double x0 = X(_points[i].TimeSec);
                double x1 = X(_points[Math.Min(j + 1, end - 1)].TimeSec);
                if (x1 - x0 >= 2)
                    dc.DrawRectangle(_idleFill, null, new Rect(x0, top, x1 - x0, plotH));
                i = j + 1;
            }
            else i++;
        }
    }

    private void DrawArea(DrawingContext dc, Func<double, double> X, Func<double, double> Y, double baseline,
        Func<Pt, double> value, Brush fill, Pen pen, int start, int end)
    {
        if (end - start < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(X(_points[start].TimeSec), baseline), true, true);
            ctx.LineTo(new Point(X(_points[start].TimeSec), Y(value(_points[start]))), true, false);
            for (int i = start + 1; i < end; i++)
                ctx.LineTo(new Point(X(_points[i].TimeSec), Y(value(_points[i]))), true, true);
            ctx.LineTo(new Point(X(_points[end - 1].TimeSec), baseline), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, null, geo);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(_points[start].TimeSec), Y(value(_points[start]))), false, false);
            for (int i = start + 1; i < end; i++)
                ctx.LineTo(new Point(X(_points[i].TimeSec), Y(value(_points[i]))), true, true);
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
