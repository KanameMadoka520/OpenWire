using System.Drawing;
using System.Drawing.Drawing2D;
using OpenWire.App.Util;
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
    public event Action<TimeSpan>? SnoozeRequested;
    public event Action? ResumeRequested;

    /// <summary>Engage a timed network lock-down for the given duration (0 = until lifted).</summary>
    public event Action<TimeSpan>? PanicRequested;
    /// <summary>Lift an active network lock-down.</summary>
    public event Action? LiftPanicRequested;

    private WinForms.ToolStripMenuItem? _resumeItem;
    private WinForms.ToolStripMenuItem? _liftPanicItem;

    public TrayService()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = Loc.S("L.Shell.TrayTooltip"),
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.S("L.Shell.TrayOpen"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var snooze = new WinForms.ToolStripMenuItem(Loc.S("L.Shell.TraySnooze"));
        snooze.DropDownItems.Add(Loc.S("L.Shell.TraySnooze30m"), null, (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromMinutes(30)));
        snooze.DropDownItems.Add(Loc.S("L.Shell.TraySnooze1h"), null, (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromHours(1)));
        snooze.DropDownItems.Add(Loc.S("L.Shell.TraySnooze8h"), null, (_, _) => SnoozeRequested?.Invoke(TimeSpan.FromHours(8)));
        menu.Items.Add(snooze);

        _resumeItem = new WinForms.ToolStripMenuItem(Loc.S("L.Shell.TrayResume"), null, (_, _) => ResumeRequested?.Invoke()) { Visible = false };
        menu.Items.Add(_resumeItem);

        var panic = new WinForms.ToolStripMenuItem(Loc.S("L.Shell.TrayPanic"));
        panic.DropDownItems.Add(Loc.S("L.Shell.TrayPanic15m"), null, (_, _) => PanicRequested?.Invoke(TimeSpan.FromMinutes(15)));
        panic.DropDownItems.Add(Loc.S("L.Shell.TrayPanic1h"), null, (_, _) => PanicRequested?.Invoke(TimeSpan.FromHours(1)));
        panic.DropDownItems.Add(Loc.S("L.Shell.TrayPanicUntil"), null, (_, _) => PanicRequested?.Invoke(TimeSpan.Zero));
        menu.Items.Add(panic);

        _liftPanicItem = new WinForms.ToolStripMenuItem(Loc.S("L.Shell.TrayLiftPanic"), null, (_, _) => LiftPanicRequested?.Invoke()) { Visible = false };
        menu.Items.Add(_liftPanicItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.S("L.Shell.TrayExit"), null, (_, _) => ExitRequested?.Invoke());
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

    /// <summary>Reflect the snooze state in the tray menu (show a Resume item) and the tooltip.</summary>
    public void SetSnoozed(bool snoozed)
    {
        if (_resumeItem is not null) _resumeItem.Visible = snoozed;
        try { _icon.Text = snoozed ? Loc.S("L.Shell.TraySnoozedTip") : Loc.S("L.Shell.TrayTooltip"); }
        catch { /* tooltip unavailable */ }
    }

    /// <summary>Show the "lift lock-down" item only while a lock-down is active.</summary>
    public void SetLockdown(bool active)
    {
        if (_liftPanicItem is not null) _liftPanicItem.Visible = active;
    }

    // The packaged app icon (Assets/app.ico); falls back to a runtime-drawn mark
    // if the resource can't be read for any reason.
    private static Icon BuildIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (res is not null)
            {
                using var s = res.Stream;
                return new Icon(s, 32, 32);
            }
        }
        catch { /* fall through to the drawn fallback */ }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var paper = new SolidBrush(Color.FromArgb(245, 245, 240));
            using var ink = new Pen(Color.FromArgb(43, 43, 43), 2.2f);
            using var node = new SolidBrush(Color.FromArgb(228, 35, 46));
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
