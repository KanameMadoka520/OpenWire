using OpenWire.App.Util;
using System.Windows;

namespace OpenWire.App.Views;

/// <summary>Ask-to-Connect prompt: a new app is blocked pending the user's Allow/Block.</summary>
public partial class FirewallPromptWindow : Window
{
    /// <summary>True = Allow, false = Block; null while undecided.</summary>
    public bool? Allowed { get; private set; }

    /// <summary>Whether the decision should be persisted in the active profile.</summary>
    public bool Remember => RememberCheck.IsChecked == true;

    public FirewallPromptWindow(string appName, string path, string remoteAddress, int remotePort)
    {
        InitializeComponent();
        NameText.Text = string.IsNullOrWhiteSpace(appName) ? Loc.S("L.Fw.PromptAppFallback") : appName;
        string peer = string.IsNullOrWhiteSpace(remoteAddress) ? string.Empty
            : string.Format(Loc.S("L.Fw.PromptRemoteFmt"), remoteAddress, remotePort);
        string detail = Loc.S("L.Fw.PromptDetail");
        DetailText.Text = string.Join("\n\n", new[] { path, peer, detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private void OnAllow(object sender, RoutedEventArgs e) { Allowed = true; Close(); }
    private void OnBlock(object sender, RoutedEventArgs e) { Allowed = false; Close(); }
}
