using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using OpenWire.App.Services;
using OpenWire.App.Util;
using OpenWire.App.ViewModels;
using OpenWire.App.Views;
using OpenWire.Core.Ipc;
using OpenWire.Core.Models;
using OpenWire.Core.Util;

namespace OpenWire.App;

public partial class App : Application
{
    public EngineClient Client { get; private set; } = null!;
    private TrayService? _tray;
    private MainWindow? _window;

    private bool _notify = true;
    private bool _minimizeToTray = true;
    private readonly HashSet<string> _promptOpen = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _snoozeUntil;              // desktop notifications suppressed until this time
    private DispatcherTimer? _snoozeTimer;

    private MainViewModel? _mainVm;
    private DispatcherTimer? _connectWatch;
    private bool _spawnAttempted; // one auto-spawn attempt per session
    private bool _userDeclined;   // user cancelled the UAC prompt — don't nag again

    private Mutex? _singleInstance;   // one app per session (the engine is bound to it via --parent-app)
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load localized resources before the privilege guard so a failed de-elevation
        // can report a usable error without ever constructing the main window.
        LangManager.Apply(this, LangManager.Read());
        ThemeManager.Apply(this, ThemeManager.Read());
        if (RefuseElevatedUi(e.Args)) return;

        // Single instance per session. The engine is tied to this app's lifetime (via --parent-app),
        // so a second window sharing the one engine would be orphaned when the first quits. Instead,
        // a second launch surfaces the running window and exits.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OpenWire.App.Show");
        var mutex = new Mutex(true, @"Local\OpenWire.App.Singleton", out bool isFirst);
        if (!isFirst)
        {
            _showEvent.Set();     // ask the running instance to come to the foreground
            mutex.Dispose();
            Shutdown();
            return;
        }
        _singleInstance = mutex;
        var showWatch = new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow); })
        { IsBackground = true, Name = "OpenWire.ShowWatch" };
        showWatch.Start();

        // Never let a stray background/UI exception take down the whole app.
        DispatcherUnhandledException += OnDispatcherException;

        Client = new EngineClient(Dispatcher);
        var vm = new MainViewModel(Client);
        _mainVm = vm;
        // The hardware / per-process samplers exist only for the Hardware page, so re-assert the
        // active signal whenever the visible section changes (not just on window show/hide).
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CurrentSection)) AssertUiActive(); };
        _window = new MainWindow { DataContext = vm };
        MainWindow = _window;

        // System tray: notifications + minimize-to-tray + Open/Exit menu.
        _tray = new TrayService();
        _tray.OpenRequested += () => Dispatcher.Invoke(ShowMainWindow);
        _tray.ExitRequested += () => Dispatcher.Invoke(Shutdown);
        _tray.SnoozeRequested += d => Dispatcher.Invoke(() => Snooze(d));
        _tray.ResumeRequested += () => Dispatcher.Invoke(ResumeNotifications);
        _tray.PanicRequested += d => Dispatcher.Invoke(() => EngagePanic(d));
        _tray.LiftPanicRequested += () => Dispatcher.Invoke(LiftPanic);

        // Keep the tray's "lift lock-down" item in sync with the engine's lock-down state.
        Client.StatusChanged += s => _tray?.SetLockdown(s.Status.LockdownActive);

        HookWindow(_window);

        // Live preferences: read on (re)connect and whenever settings are saved.
        Client.ConnectionChanged += connected =>
        {
            if (!connected) return;
            _connectWatch?.Stop();      // an engine answered; no need to spawn one
            _ = RefreshPrefsAsync();
            AssertUiActive();           // report the real active state on every (re)connect
        };
        vm.Settings.Saved += s => { _notify = s.ShowDesktopNotifications; _minimizeToTray = s.MinimizeToTray; };

        // Desktop notifications for noteworthy (non-informational) alerts.
        Client.AlertRaised += a =>
        {
            if (_notify && DateTimeOffset.Now >= _snoozeUntil && a.Alert.Severity != AlertSeverity.Info)
                _tray?.Notify(a.Alert.Title, a.Alert.Message, a.Alert.Severity == AlertSeverity.Critical);
        };

        // Ask-to-Connect prompt for a newly-blocked app awaiting a decision.
        Client.FirewallPrompt += ev => Dispatcher.BeginInvoke(() => ShowFirewallPrompt(ev));

        _window.Show();
        Client.Start();

        // If no engine answers within a few seconds, spawn one (elevated). Cancelled on connect.
        _connectWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _connectWatch.Tick += (_, _) => { _connectWatch!.Stop(); TryAutoSpawnEngine(); };
        _connectWatch.Start();
    }

    private bool RefuseElevatedUi(string[] args)
    {
        if (!IsCurrentProcessElevated())
            return false;

        // Preserve the user's old startup preference while deleting the legacy task
        // before it can launch another high-integrity UI at the next sign-in.
        if (LegacyAutoStartTask.RemoveIfPresent())
            UserAutoStartManager.Configure(enabled: true);

        if (!args.Contains("--unelevated-relaunch", StringComparer.OrdinalIgnoreCase)
            && TryRelaunchUnelevated())
        {
            Shutdown();
            return true;
        }

        MessageBox.Show(
            Loc.S("L.Shell.ElevatedUiBlocked"),
            "OpenWire",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown();
        return true;
    }

    private static bool IsCurrentProcessElevated()
    {
        if (IpcPeerIdentity.TryGetProcessInfo(Environment.ProcessId, out var self))
            return self.IsElevated;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // An indeterminate token must not be allowed to keep a desktop UI at high integrity.
            return true;
        }
    }

    private static bool TryRelaunchUnelevated()
    {
        try
        {
            string? app = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(app) || !File.Exists(app)) return false;
            string explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = $"\"{app}\" --unelevated-relaunch",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void HookWindow(MainWindow w)
    {
        w.StateChanged += (_, _) =>
        {
            if (w.WindowState == WindowState.Minimized && _minimizeToTray)
                w.Hide();
            AssertUiActive(); // hidden/minimized -> throttle the samplers; restored -> full rate
        };
    }

    /// <summary>Full-rate hardware / per-process sampling is worthwhile only when the Hardware page
    /// is actually on screen — its window shown and that section selected. Every other tab, and the
    /// tray, leaves the engine's samplers idle (the process sweep pauses, hardware goes coarse).</summary>
    private bool ComputeUiActive() =>
        _window is { IsVisible: true, WindowState: not WindowState.Minimized }
        && _mainVm?.CurrentSection == Section.Hardware;

    private void AssertUiActive()
    {
        if (Client.IsConnected) Client.SetUiActive(ComputeUiActive());
    }

    /// <summary>Suppress desktop notifications for a while (from the tray menu). A one-shot timer
    /// clears the state when the window elapses so the tray reflects it; alerts still accrue in the log.</summary>
    private void Snooze(TimeSpan duration)
    {
        _snoozeUntil = DateTimeOffset.Now + duration;
        _tray?.SetSnoozed(true);
        _snoozeTimer?.Stop();
        _snoozeTimer = new DispatcherTimer { Interval = duration };
        _snoozeTimer.Tick += (_, _) =>
        {
            _snoozeTimer!.Stop();
            if (DateTimeOffset.Now >= _snoozeUntil) ResumeNotifications();
        };
        _snoozeTimer.Start();
    }

    private void ResumeNotifications()
    {
        _snoozeTimer?.Stop();
        _snoozeUntil = default;
        _tray?.SetSnoozed(false);
    }

    /// <summary>Engage a timed network lock-down from the tray (0 = until manually lifted).</summary>
    private async void EngagePanic(TimeSpan duration)
    {
        try { await Client.SetLockdownAsync(true, (long)duration.TotalSeconds); }
        catch { /* engine unavailable / not elevated — the tray item is best-effort */ }
    }

    private async void LiftPanic()
    {
        try { await Client.SetLockdownAsync(false); }
        catch { /* engine unavailable */ }
    }

    /// <summary>When no engine is reachable, start one once (elevated — the engine needs admin for
    /// ETW + firewall). Latched so a missing exe or a declined UAC prompt never loops.</summary>
    private void TryAutoSpawnEngine()
    {
        if (Client.IsConnected || _userDeclined) return;
        // An engine process already exists (starting up, or a stale one). Don't spawn a duplicate —
        // the engine's single-instance mutex would reject it anyway, costing a needless UAC prompt —
        // but keep re-checking so we still connect once it's listening, or spawn if it later dies.
        if (System.Diagnostics.Process.GetProcessesByName("OpenWire.Service").Length > 0)
        {
            _connectWatch?.Start();
            return;
        }
        if (_spawnAttempted) return;
        _spawnAttempted = true;
        try { Services.EngineLauncher.SpawnService(runas: true); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _userDeclined = true; }
        catch { /* exe missing / other — the manual "Start engine" button remains */ }
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
        AssertUiActive();
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
        try { UiCrashLog.Write(e.Exception); }
        catch { /* logging is best-effort */ }
        e.Handled = true; // keep the app alive
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Client?.Dispose();
        _singleInstance?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
