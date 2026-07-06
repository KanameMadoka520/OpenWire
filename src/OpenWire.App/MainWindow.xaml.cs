using System.Windows;
using OpenWire.App.Util;

namespace OpenWire.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowFx.ApplyRoundedCorners(this);
        // Compact top bar: below this width the tabs drop their text labels
        // (icon-only) so the bar never clips. The TopTab template watches Tag.
        SizeChanged += (_, _) => Tag = ActualWidth < 1200 ? "compact" : null;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
