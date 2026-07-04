using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace OpenWire.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* no browser / blocked — ignore */ }
        e.Handled = true;
    }
}
