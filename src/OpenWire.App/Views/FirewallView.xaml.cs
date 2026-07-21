using System.Windows;
using System.Windows.Controls;
using OpenWire.App.Util;
using OpenWire.App.ViewModels;

namespace OpenWire.App.Views;

public partial class FirewallView : UserControl
{
    private FirewallViewModel? _vm;

    public FirewallView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.EditQuotaRequested -= OnEditQuota;
        _vm = e.NewValue as FirewallViewModel;
        if (_vm is not null) _vm.EditQuotaRequested += OnEditQuota;
    }

    private void OnPanicMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void OnEditQuota(AppRowVM row)
    {
        var dlg = new QuotaDialog(row.AppId, row.Name, row.Path, row.Quota);
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        dlg.ShowDialog();

        if (_vm is null) return;
        while (true)
        {
            try
            {
                switch (dlg.Result)
                {
                    case QuotaDialog.DialogResultKind.Save:
                        await _vm.SaveQuotaAsync(row.AppId, dlg.Quota);
                        break;
                    case QuotaDialog.DialogResultKind.Remove:
                        await _vm.SaveQuotaAsync(row.AppId, null);
                        break;
                }
                break;
            }
            catch (FirewallViewModel.QuotaRefreshException ex)
            {
                MessageBox.Show(
                    string.Format(Loc.S("L.Fw.QuotaRefreshErrorFmt"), ex.InnerException?.Message ?? ex.Message),
                    Loc.S("L.Fw.QuotaTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
            catch (Exception ex)
            {
                var retry = MessageBox.Show(
                    string.Format(Loc.S("L.Fw.QuotaSaveErrorFmt"), ex.Message),
                    Loc.S("L.Fw.QuotaTitle"), MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (retry != MessageBoxResult.Yes) break;
            }
        }
    }
}
