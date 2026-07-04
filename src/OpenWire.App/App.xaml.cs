using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenWire.App.Services;
using OpenWire.App.ViewModels;

namespace OpenWire.App;

public partial class App : Application
{
    public EngineClient Client { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Never let a stray background/UI exception take down the whole app.
        DispatcherUnhandledException += OnDispatcherException;

        Client = new EngineClient(Dispatcher);
        var vm = new MainViewModel(Client);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        Client.Start();
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
        Client?.Dispose();
        base.OnExit(e);
    }
}
