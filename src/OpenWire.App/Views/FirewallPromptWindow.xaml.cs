using System.Windows;

namespace OpenWire.App.Views;

/// <summary>Ask-to-Connect prompt: a new app is blocked pending the user's Allow/Block.</summary>
public partial class FirewallPromptWindow : Window
{
    /// <summary>True = Allow, false = Block; null while undecided.</summary>
    public bool? Allowed { get; private set; }

    public FirewallPromptWindow(string appName, string path)
    {
        InitializeComponent();
        NameText.Text = string.IsNullOrWhiteSpace(appName) ? "An application" : appName;
        DetailText.Text = string.IsNullOrWhiteSpace(path)
            ? "This app reached the network for the first time. Allow it, or block it until you decide."
            : $"{path}\n\nThis app reached the network for the first time. Allow it, or block it until you decide.";
    }

    private void OnAllow(object sender, RoutedEventArgs e) { Allowed = true; Close(); }
    private void OnBlock(object sender, RoutedEventArgs e) { Allowed = false; Close(); }
}
