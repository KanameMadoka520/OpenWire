using System.Windows;
using System.Windows.Media;

namespace OpenWire.App.Controls;

/// <summary>
/// A half-ring gauge split into a download (yellow) and upload (pink) arc,
/// proportional to <see cref="DownValue"/> and <see cref="UpValue"/>.
/// </summary>
public sealed class GaugeArc : FrameworkElement
{
    public static readonly DependencyProperty DownValueProperty =
        DependencyProperty.Register(nameof(DownValue), typeof(double), typeof(GaugeArc),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpValueProperty =
        DependencyProperty.Register(nameof(UpValue), typeof(double), typeof(GaugeArc),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double DownValue { get => (double)GetValue(DownValueProperty); set => SetValue(DownValueProperty, value); }
    public double UpValue { get => (double)GetValue(UpValueProperty); set => SetValue(UpValueProperty, value); }

    private readonly Pen _track;
    private readonly Pen _down;
    private readonly Pen _up;

    public GaugeArc()
    {
        _track = MakePen(Res("BorderColor", Color.FromRgb(0xE7, 0xEA, 0xEF)));
        _down = MakePen(Res("InFillColor", Color.FromRgb(0xF7, 0xC9, 0x48)));
        _up = MakePen(Res("OutFillColor", Color.FromRgb(0xF9, 0x7C, 0xA8)));
    }

    private static Pen MakePen(Color c)
    {
        var p = new Pen(new SolidColorBrush(c), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        double cx = w / 2, cy = h - 8;
        double r = Math.Min(w / 2, h) - 12;
        if (r <= 0) return;

        double total = DownValue + UpValue;
        double downFrac = total > 0 ? DownValue / total : 0.5;
        double split = 180 - downFrac * 180;

        dc.DrawGeometry(null, _track, Arc(cx, cy, r, 180, 0));
        if (DownValue > 0) dc.DrawGeometry(null, _down, Arc(cx, cy, r, 180, split));
        if (UpValue > 0) dc.DrawGeometry(null, _up, Arc(cx, cy, r, split, 0));
    }

    private static Geometry Arc(double cx, double cy, double r, double startDeg, double endDeg)
    {
        Point p0 = Polar(cx, cy, r, startDeg);
        Point p1 = Polar(cx, cy, r, endDeg);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Point Polar(double cx, double cy, double r, double deg)
    {
        double rad = deg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
    }

    private static Color Res(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
}
