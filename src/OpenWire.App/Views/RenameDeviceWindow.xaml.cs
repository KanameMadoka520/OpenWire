using System.Windows;

namespace OpenWire.App.Views;

/// <summary>Small modal prompt to give a LAN device a friendly name.</summary>
public partial class RenameDeviceWindow : Window
{
    /// <summary>The confirmed new name, or null if the dialog was cancelled.</summary>
    public string? NewName { get; private set; }

    public RenameDeviceWindow(string current)
    {
        InitializeComponent();
        NameBox.Text = current ?? "";
        Loaded += (_, _) => { NameBox.SelectAll(); NameBox.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { NameBox.Focus(); return; }
        NewName = name;
        DialogResult = true;
    }
}
