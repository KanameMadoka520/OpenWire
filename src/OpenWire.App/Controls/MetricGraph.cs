using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWire.Core.Util;

namespace OpenWire.App.Controls;

/// <summary>A single-series filled line graph for the Hardware Resources screen
/// (CPU / memory / disk / GPU). Auto-scales to the visible peak and prints the
/// scale on the right, GlassWire-style.</summary>
public sealed class MetricGraph : FrameworkElement
{
    private double[] _values = Array.Empty<double>();
    private double _max = 1;
    private bool _bytes;

    private readonly Brush _fill;
    private readonly Pen _line;
    private readonly Pen _grid;
    private readonly Brush _text;
    private readonly Typeface _mono = new("Cascadia Mono, Consolas");

    public MetricGraph()
    {
        var accent = Color.FromRgb(0x6F, 0x9F, 0xE8);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, accent.R, accent.G, accent.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x0E, accent.R, accent.G, accent.B), 1));
        b.Freeze();
        _fill = b;
        _line = new Pen(new SolidColorBrush(accent), 1.4); _line.Freeze();
        _grid = new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)), 1); _grid.Freeze();
        _text = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)); _text.Freeze();
    }

    /// <summary>Set the series; the graph auto-scales to the visible peak.</summary>
    public void SetValues(IReadOnlyList<double> values, bool bytes, double? unitScale = null)
    {
        _values = values.ToArray();
        _bytes = bytes;
        double peak = _values.DefaultIfEmpty(0).Max();
        double floor = unitScale ?? (bytes ? 1_000_000 : 10);
        _max = Math.Max(peak * 1.15, floor);
        InvalidateVisual();
    }

    private string Fmt(double v) => _bytes ? ByteFormatter.Rate(v) : $"{v:0} %";

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        const double padTop = 6, padBottom = 4;
        double plotH = h - padTop - padBottom;

        // top + mid gridlines
        dc.DrawLine(_grid, new Point(0, padTop), new Point(w, padTop));
        dc.DrawLine(_grid, new Point(0, padTop + plotH / 2), new Point(w, padTop + plotH / 2));

        // scale label on the RIGHT (the auto-scaled peak)
        var ft = new FormattedText(Fmt(_max), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _mono, 11, _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(w - ft.Width - 4, 1));

        if (_values.Length < 2) return;

        double dx = w / Math.Max(1, _values.Length - 1);
        double Y(double v) => padTop + plotH - Math.Min(1.0, v / _max) * plotH;

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h - padBottom), true, true);
            ctx.LineTo(new Point(0, Y(_values[0])), true, false);
            for (int i = 1; i < _values.Length; i++)
                ctx.LineTo(new Point(i * dx, Y(_values[i])), true, true);
            ctx.LineTo(new Point((_values.Length - 1) * dx, h - padBottom), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(_fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(0, Y(_values[0])), false, false);
            for (int i = 1; i < _values.Length; i++)
                ctx.LineTo(new Point(i * dx, Y(_values[i])), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, _line, line);
    }
}
