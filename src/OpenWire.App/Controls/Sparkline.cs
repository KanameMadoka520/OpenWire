using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace OpenWire.App.Controls;

/// <summary>
/// A compact filled-area micro-chart of an application's recent per-second
/// throughput, auto-scaled to its own peak. Bound to the app's rolling rate
/// history so the Firewall list shows each app's activity "shape" at a glance.
/// Renders nothing when there is no movement (flat/empty history).
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<int>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((Sparkline)d).InvalidateVisual()));

    public IEnumerable<int>? Values
    {
        get => (IEnumerable<int>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    private readonly Brush _fill;
    private readonly Pen _line;
    private readonly Pen _baseline;

    public Sparkline()
    {
        Color a = ResColor("AccentColor", Color.FromRgb(0x3B, 0x82, 0xF6));
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x4D, a.R, a.G, a.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x0D, a.R, a.G, a.B), 1));
        fill.Freeze();
        _fill = fill;
        _line = new Pen(new SolidColorBrush(a), 1.3); _line.Freeze();
        _baseline = new Pen(new SolidColorBrush(ResColor("BorderColor", Color.FromRgb(0xE7, 0xEA, 0xEF))), 1); _baseline.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var vals = Values as IList<int> ?? Values?.ToList();
        double baseY = h - 2;
        if (vals is null || vals.Count < 2 || vals.Max() <= 0)
        {
            dc.DrawLine(_baseline, new Point(0, baseY), new Point(w, baseY));
            return;
        }

        double max = vals.Max();
        const double pad = 2;
        double plotH = h - pad * 2;
        int n = vals.Count;
        double X(int i) => (double)i / (n - 1) * w;
        double Y(int v) => pad + plotH - v / max * plotH;

        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(X(0), baseY), true, true);
            c.LineTo(new Point(X(0), Y(vals[0])), true, false);
            for (int i = 1; i < n; i++) c.LineTo(new Point(X(i), Y(vals[i])), true, true);
            c.LineTo(new Point(X(n - 1), baseY), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(_fill, null, area);

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(new Point(X(0), Y(vals[0])), false, false);
            for (int i = 1; i < n; i++) c.LineTo(new Point(X(i), Y(vals[i])), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, _line, line);
    }

    private static Color ResColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;
}
