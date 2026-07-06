using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenWire.App.Services;
using OpenWire.App.ViewModels;
using OpenWire.App.Views;
using OpenWire.Core.Ipc;
using OpenWire.Core.Models;

namespace OpenWire.App;

public partial class App : Application
{
    public EngineClient Client { get; private set; } = null!;
    private TrayService? _tray;
    private MainWindow? _window;

    private bool _notify = true;
    private bool _minimizeToTray = true;
    private readonly HashSet<string> _promptOpen = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply the chosen language + skin before any window is created.
        LangManager.Apply(this, LangManager.Read());
        ThemeManager.Apply(this, ThemeManager.Read());

        // Never let a stray background/UI exception take down the whole app.
        DispatcherUnhandledException += OnDispatcherException;

        Client = new EngineClient(Dispatcher);
        var vm = new MainViewModel(Client);
        _window = new MainWindow { DataContext = vm };
        MainWindow = _window;

        // System tray: notifications + minimize-to-tray + Open/Exit menu.
        _tray = new TrayService();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowMainWindow);
        _tray.ExitRequested += () => Dispatcher.Invoke(Shutdown);

        HookWindow(_window);

        // Live preferences: read on (re)connect and whenever settings are saved.
        Client.ConnectionChanged += connected => { if (connected) _ = RefreshPrefsAsync(); };
        vm.Settings.Saved += s => { _notify = s.ShowDesktopNotifications; _minimizeToTray = s.MinimizeToTray; };

        // Desktop notifications for noteworthy (non-informational) alerts.
        Client.AlertRaised += a =>
        {
            if (_notify && a.Alert.Severity != AlertSeverity.Info)
                _tray?.Notify(a.Alert.Title, a.Alert.Message, a.Alert.Severity == AlertSeverity.Critical);
        };

        // Ask-to-Connect prompt for a newly-blocked app awaiting a decision.
        Client.FirewallPrompt += ev => Dispatcher.BeginInvoke(() => ShowFirewallPrompt(ev));

        _window.Show();
        Client.Start();
    }

    private void HookWindow(MainWindow w)
    {
        w.StateChanged += (_, _) =>
        {
            if (w.WindowState == WindowState.Minimized && _minimizeToTray)
                w.Hide();
        };
    }

    /// <summary>
    /// Re-merge the language + skin dictionaries and rebuild the main window in
    /// place (same view-models, same position). StaticResource lookups resolve at
    /// XAML load time, so a fresh window is what makes new dictionaries take
    /// everywhere — this is live theme/language switching without an app restart
    /// (the engine and the tray keep running).
    /// </summary>
    public void ReskinMainWindow() => Dispatcher.BeginInvoke(ReskinMainWindowCore);

    private void ReskinMainWindowCore()
    {
        // Deferred to the dispatcher: the switch is triggered from a radio-button
        // event inside the old window — closing it mid-routed-event is asking for
        // re-entrancy trouble.
        if (_window is null) return;
        var old = _window;
        var vm = old.DataContext;

        Resources.MergedDictionaries.Clear();
        LangManager.Apply(this, LangManager.Read());
        ThemeManager.Apply(this, ThemeManager.Read());

        var w = new MainWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left,
            Top = old.Top,
            Width = old.Width,
            Height = old.Height,
        };
        if (old.WindowState == WindowState.Maximized) w.WindowState = WindowState.Maximized;

        _window = w;
        MainWindow = w;
        HookWindow(w);
        w.Show();
        old.Close();
    }

    private async Task RefreshPrefsAsync()
    {
        try
        {
            var s = (await Client.GetSettingsAsync()).Settings;
            _notify = s.ShowDesktopNotifications;
            _minimizeToTray = s.MinimizeToTray;
        }
        catch { /* transient */ }
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowFirewallPrompt(FirewallPromptEvent ev)
    {
        if (ev.App is null || string.IsNullOrEmpty(ev.App.Id)) return;
        if (!_promptOpen.Add(ev.App.Id)) return; // already asking about this app

        try
        {
            var dlg = new FirewallPromptWindow(ev.App.Name, ev.App.ExecutablePath);
            if (_window is { IsVisible: true }) dlg.Owner = _window;
            dlg.ShowDialog();
            if (dlg.Allowed is bool allow)
                _ = Client.ResolveAppDecisionAsync(ev.App.Id, allow);
        }
        finally { _promptOpen.Remove(ev.App.Id); }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "OpenWire-ui.log"),
                $"{DateTimeOffset.Now:O}  {e.Exception}\n\n");
        }
        catch { /* logging is best-effort */ }
        e.Handled = true; // keep the app alive
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Client?.Dispose();
        base.OnExit(e);
    }
}
