using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>
/// A compact vertical bar chart for the Analytics screen (hour-of-day and per-day
/// usage). Auto-scales to the peak, highlights one bar, prints the peak scale on the
/// right and a strided set of axis labels along the bottom — matching the MetricGraph
/// aesthetic.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    private double[] _values = Array.Empty<double>();
    private string[] _labels = Array.Empty<string>();
    private int _highlight = -1;
    private double _max = 1;
    private bool _bytes = true;

    private readonly Brush _bar;
    private readonly Brush _barHi;
    private readonly Pen _grid;
    private readonly Brush _text;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");
    private readonly Typeface _ui = new("Segoe UI Variable Text, Segoe UI");

    public BarChart()
    {
        var accent = Color.FromRgb(0x3F, 0x6C, 0x8C);
        _bar = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B)); _bar.Freeze();
        _barHi = new SolidColorBrush(accent); _barHi.Freeze();
        _grid = new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xCF, 0xBA)), 1); _grid.Freeze();
        _text = new SolidColorBrush(Color.FromRgb(0x87, 0x7E, 0x6C)); _text.Freeze();
    }

    /// <summary>Feed a labelled series. <paramref name="highlight"/> draws one bar solid.</summary>
    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string> labels, int highlight, bool bytes)
    {
        _values = values.ToArray();
        _labels = labels.ToArray();
        _highlight = highlight;
        _bytes = bytes;
        double peak = _values.DefaultIfEmpty(0).Max();
        _max = Math.Max(peak * 1.15, bytes ? 1_000_000 : 1);
        InvalidateVisual();
    }

    private string Fmt(double v) => _bytes ? ByteFormatter.Bytes((long)v) : v.ToString("0");

    // Redraw whenever the control is (re)sized, so data set while the control had zero
    // size (e.g. fed during a tab switch before first layout) still paints once laid out.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4 || _values.Length == 0) return;

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
                dc.DrawRoundedRectangle(i == _highlight ? _barHi : _bar, null, new Rect(x, y, barW, bh), 2, 2);

            if (i < _labels.Length && i % stride == 0 && !string.IsNullOrEmpty(_labels[i]))
            {
                var lbl = new FormattedText(_labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _ui, 10, _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double lx = i * slot + (slot - lbl.Width) / 2;
                dc.DrawText(lbl, new Point(Math.Max(0, Math.Min(w - lbl.Width, lx)), padTop + plotH + 4));
            }
        }
    }
}
