using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace OpenWire.App.Services;

/// <summary>
/// Owns the system-tray icon: balloon notifications and a small context menu
/// (Open / Exit). All the WinForms/Win32 interop is isolated here so the rest of
/// the app stays pure WPF. The tray glyph is drawn at runtime (no asset needed).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = "OpenWire — network monitor",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open OpenWire", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Show a balloon/toast notification from the tray.</summary>
    public void Notify(string title, string message, bool warning)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(6000);
        }
        catch { /* shell notifications unavailable */ }
    }

    // A tiny hand-drawn "wire" mark on a paper chip — original artwork, drawn at runtime.
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var paper = new SolidBrush(Color.FromArgb(245, 245, 240));
            using var ink = new Pen(Color.FromArgb(43, 43, 43), 2.2f);
            using var node = new SolidBrush(Color.FromArgb(59, 130, 246));
            g.FillRectangle(paper, 3, 3, 25, 25);
            g.DrawRectangle(ink, 3, 3, 25, 25);
            g.DrawLines(ink, new[] { new PointF(8, 21), new PointF(13, 12), new PointF(19, 21), new PointF(24, 11) });
            g.FillEllipse(node, 21, 8, 5, 5);
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        return (Icon)tmp.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
