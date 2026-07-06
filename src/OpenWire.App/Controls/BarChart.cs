using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenWire.App.Util;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// A compact vertical bar chart for the Analytics screen (hour-of-day and per-day
/// usage). Auto-scales to the peak, highlights one bar, prints the peak scale on the
/// right and a strided set of axis labels along the bottom — matching the MetricGraph
/// aesthetic. Hovering a bar lifts it and floats a tooltip with the exact figure, the
/// download/upload split, and the top applications behind that bar.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    /// <summary>One application's slice of a bar, for the hover tooltip.</summary>
    public readonly record struct BarApp(string Name, string Path, long Bytes);

    /// <summary>Extra per-bar figures surfaced on hover (download/upload split + top apps).</summary>
    public sealed class BarDetail
    {
        /// <summary>Tooltip heading (e.g. "06:00" or "Jul 6"); falls back to the axis label.</summary>
        public string Header { get; init; } = "";
        public long BytesIn { get; init; }
        public long BytesOut { get; init; }
        public IReadOnlyList<BarApp> TopApps { get; init; } = Array.Empty<BarApp>();
    }

    private double[] _values = Array.Empty<double>();
    private string[] _labels = Array.Empty<string>();
    private BarDetail[] _details = Array.Empty<BarDetail>();
    private int _highlight = -1;
    private int _hover = -1;
    private double _max = 1;
    private bool _bytes = true;

    // Segoe Fluent down/up glyphs, built by code point (no private-use chars in source).
    private static readonly string DownGlyph = ((char)0xE74B).ToString();
    private static readonly string UpGlyph = ((char)0xE74A).ToString();

    private readonly Brush _bar;
    private readonly Brush _barHi;
    private readonly Pen _grid;
    private readonly Brush _text;
    private readonly Brush _tipBg;
    private readonly Brush _tipText;
    private readonly Brush _tipMuted;
    private readonly Brush _inLine;
    private readonly Brush _outLine;
    private readonly Pen _tipBorder;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");
    private readonly Typeface _iconFont = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly IconLoader Icons = new();

    public BarChart()
    {
        var accent = ResColor("AccentColor", Color.FromRgb(0x3F, 0x6C, 0x8C));
        _bar = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B)); _bar.Freeze();
        _barHi = new SolidColorBrush(accent); _barHi.Freeze();
        _grid = new Pen(new SolidColorBrush(ResColor("GridLineColor", Color.FromRgb(0xE4, 0xE7, 0xEB))), 1); _grid.Freeze();
        _text = new SolidColorBrush(ResColor("GridTextColor", Color.FromRgb(0x79, 0x81, 0x8B))); _text.Freeze();

        _tipBg = ResBrush("BgPanel", Color.FromRgb(0xFF, 0xFF, 0xFF));
        _tipText = ResBrush("TextPrimary", Color.FromRgb(0x23, 0x27, 0x2D));
        _tipMuted = ResBrush("TextSecondary", Color.FromRgb(0x52, 0x59, 0x63));
        _inLine = ResBrush("InLine", Color.FromRgb(0xB0, 0x8A, 0x2C));
        _outLine = ResBrush("OutLine", Color.FromRgb(0xC0, 0x5C, 0x80));
        _tipBorder = new Pen(ResBrush("CtlBorderBrush", Color.FromRgb(0x8B, 0x92, 0x9B)), 1); _tipBorder.Freeze();
    }

    /// <summary>Feed a labelled series. <paramref name="highlight"/> draws one bar solid;
    /// <paramref name="details"/> (optional, same order as <paramref name="values"/>) powers the
    /// hover tooltip's split + per-app breakdown.</summary>
    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string> labels, int highlight, bool bytes,
        IReadOnlyList<BarDetail>? details = null)
    {
        _values = values.ToArray();
        _labels = labels.ToArray();
        _details = details?.ToArray() ?? Array.Empty<BarDetail>();
        _highlight = highlight;
        _bytes = bytes;
        _hover = -1;
        double peak = _values.DefaultIfEmpty(0).Max();
        _max = Math.Max(peak * 1.15, bytes ? 1_000_000 : 1);
        InvalidateVisual();
    }

    private static Color ResColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    private static Brush ResBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var s = new SolidColorBrush(fallback); s.Freeze(); return s;
    }

    private string Fmt(double v) => _bytes ? ByteFormatter.Bytes((long)v) : v.ToString("0");

    // Redraw whenever the control is (re)sized, so data set while the control had zero
    // size (e.g. fed during a tab switch before first layout) still paints once laid out.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    // Horizontal slot width per bar — shared by paint + hit-testing.
    private double Slot => _values.Length == 0 ? 0 : (ActualWidth - 2) / _values.Length;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double slot = Slot;
        int idx = slot <= 0 ? -1 : (int)(e.GetPosition(this).X / slot);
        if (idx < 0 || idx >= _values.Length) idx = -1;
        if (idx != _hover) { _hover = idx; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4 || _values.Length == 0) return;

        // Transparent fill makes the whole surface hit-testable (so hover works over gaps too).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        const double padTop = 8, padBottom = 18, padRight = 2;
        double plotH = h - padTop - padBottom;
        double plotW = w - padRight;
        if (plotH <= 2) return;

        // gridlines (top / mid / base)
        dc.DrawLine(_grid, new Point(0, padTop), new Point(w, padTop));
        dc.DrawLine(_grid, new Point(0, padTop + plotH / 2), new Point(w, padTop + plotH / 2));
        dc.DrawLine(_grid, new Point(0, padTop + plotH), new Point(w, padTop + plotH));

        // peak scale label, top-right
        var scale = new FormattedText(Fmt(_max), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _mono, 10.5, _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(scale, new Point(w - scale.Width - 3, 0));

        int n = _values.Length;
        double slot = plotW / n;
        double barW = Math.Max(1, slot * 0.62);
        double gap = (slot - barW) / 2;

        // label stride so labels never crowd (≥ ~46px apart)
        int stride = Math.Max(1, (int)Math.Ceiling(46.0 / slot));

        for (int i = 0; i < n; i++)
        {
            double bh = Math.Min(1.0, _values[i] / _max) * plotH;
            double x = i * slot + gap;
            double y = padTop + plotH - bh;
            if (bh > 0.5)
            {
                bool solid = i == _highlight || i == _hover;
                dc.DrawRoundedRectangle(solid ? _barHi : _bar, null, new Rect(x, y, barW, bh), 2, 2);
            }

            if (i < _labels.Length && i % stride == 0 && !string.IsNullOrEmpty(_labels[i]))
            {
                var lbl = new FormattedText(_labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _ui, 10, _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double lx = i * slot + (slot - lbl.Width) / 2;
                dc.DrawText(lbl, new Point(Math.Max(0, Math.Min(w - lbl.Width, lx)), padTop + plotH + 4));
            }
        }

        if (_hover >= 0 && _hover < n)
            DrawTooltip(dc, _hover, slot, gap, barW, plotH, padTop, w, h);
    }

    private void DrawTooltip(DrawingContext dc, int i, double slot, double gap, double barW,
        double plotH, double padTop, double w, double h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText FT(string s, Typeface tf, double size, Brush b, double? maxW = null)
        {
            var t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, dpi);
            if (maxW is double m) { t.MaxTextWidth = m; t.MaxLineCount = 1; t.Trimming = TextTrimming.CharacterEllipsis; }
            return t;
        }

        const double pad = 10, iconSz = 14, iconGap = 7, rowGap = 4, colGap = 16, nameMax = 170;
        var detail = i < _details.Length ? _details[i] : null;

        // header: label (tooltip-specific header if provided) + exact figure
        string label = detail is not null && detail.Header.Length > 0 ? detail.Header
            : (i < _labels.Length && _labels[i].Length > 0 ? _labels[i] : Fmt(_values[i]));
        var head = FT(label, _ui, 12, _tipText);
        var total = FT(Fmt(_values[i]), _mono, 15, _tipText);

        // download / upload split line (colour-coded glyph + value)
        FormattedText? inGlyph = null, inVal = null, outGlyph = null, outVal = null;
        double splitW = 0, splitH = 0;
        if (detail is not null)
        {
            inGlyph = FT(DownGlyph, _iconFont, 10, _inLine);
            inVal = FT(ByteFormatter.Bytes(detail.BytesIn), _mono, 11, _tipMuted);
            outGlyph = FT(UpGlyph, _iconFont, 10, _outLine);
            outVal = FT(ByteFormatter.Bytes(detail.BytesOut), _mono, 11, _tipMuted);
            splitW = inGlyph.Width + 4 + inVal.Width + colGap + outGlyph.Width + 4 + outVal.Width;
            splitH = Math.Max(inVal.Height, outVal.Height);
        }

        // top-app rows: icon · name · bytes
        var rows = new List<(ImageSource? Icon, FormattedText Name, FormattedText Bytes)>();
        double rowsW = 0, rowH = 0;
        if (detail is not null)
        {
            foreach (var a in detail.TopApps)
            {
                var name = FT(a.Name, _ui, 11.5, _tipText, nameMax);
                var bytes = FT(ByteFormatter.Bytes(a.Bytes), _mono, 10.5, _tipMuted);
                var icon = string.IsNullOrEmpty(a.Path) ? null
                    : Icons.Convert(a.Path, typeof(ImageSource), null, CultureInfo.InvariantCulture) as ImageSource;
                rows.Add((icon, name, bytes));
                rowsW = Math.Max(rowsW, iconSz + iconGap + name.Width + colGap + bytes.Width);
                rowH = Math.Max(rowH, Math.Max(iconSz, name.Height));
            }
        }

        // box size
        double contentW = Math.Max(Math.Max(head.Width, total.Width), Math.Max(splitW, rowsW));
        // Never let the width go non-positive (a 5–6px-wide plot makes w-6 ≤ 0), which would throw
        // when constructing the Rect below.
        double boxW = Math.Max(1, Math.Min(w - 6, contentW + pad * 2));
        double boxH = pad + head.Height + 3 + total.Height
                    + (detail is not null ? 6 + splitH : 0)
                    + (rows.Count > 0 ? 8 + rows.Count * (rowH + rowGap) - rowGap : 0)
                    + pad;

        // position: above the hovered bar, clamped inside the plot
        double bh = Math.Min(1.0, _values[i] / _max) * plotH;
        double barTop = padTop + plotH - bh;
        double centerX = i * slot + gap + barW / 2;
        double boxX = Math.Max(2, Math.Min(w - boxW - 2, centerX - boxW / 2));
        double boxY = barTop - boxH - 8;
        if (boxY < 2) boxY = Math.Min(h - boxH - 2, barTop + 8);
        boxY = Math.Max(2, boxY);

        var box = new Rect(boxX, boxY, boxW, boxH);
        dc.DrawRoundedRectangle(_tipBg, _tipBorder, box, 7, 7);

        double cx = boxX + pad, cy = boxY + pad;
        dc.DrawText(head, new Point(cx, cy)); cy += head.Height + 3;
        dc.DrawText(total, new Point(cx, cy)); cy += total.Height;

        if (detail is not null && inGlyph is not null)
        {
            cy += 6;
            double sx = cx;
            dc.DrawText(inGlyph, new Point(sx, cy + 1)); sx += inGlyph.Width + 4;
            dc.DrawText(inVal!, new Point(sx, cy)); sx += inVal!.Width + colGap;
            dc.DrawText(outGlyph!, new Point(sx, cy + 1)); sx += outGlyph!.Width + 4;
            dc.DrawText(outVal!, new Point(sx, cy));
            cy += splitH;
        }

        if (rows.Count > 0)
        {
            cy += 8;
            foreach (var (icon, name, bytes) in rows)
            {
                if (icon is not null)
                    dc.DrawImage(icon, new Rect(cx, cy + (rowH - iconSz) / 2, iconSz, iconSz));
                dc.DrawText(name, new Point(cx + iconSz + iconGap, cy + (rowH - name.Height) / 2));
                dc.DrawText(bytes, new Point(boxX + boxW - pad - bytes.Width, cy + (rowH - bytes.Height) / 2));
                cy += rowH + rowGap;
            }
        }
    }
}
