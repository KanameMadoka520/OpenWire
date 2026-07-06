using System.Windows;
using System.Windows.Controls;
using OpenWire.App.ViewModels;

namespace OpenWire.App.Views;

public partial class ThingsView : UserControl
{
    public ThingsView()
    {
        InitializeComponent();
        // GridView headers have no Command, so catch their bubbling Click here.
        DeviceList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    /// <summary>Per-column sort key, set in XAML as local:ThingsView.SortField="name".
    /// Columns without it (the trailing forget-button column) are not sortable.</summary>
    public static readonly DependencyProperty SortFieldProperty =
        DependencyProperty.RegisterAttached(
            "SortField", typeof(string), typeof(ThingsView), new PropertyMetadata(null));

    public static void SetSortField(DependencyObject o, string value) => o.SetValue(SortFieldProperty, value);
    public static string? GetSortField(DependencyObject o) => (string?)o.GetValue(SortFieldProperty);

    // Clicking a column header sorts by that column (repeat clicks reverse it). The
    // sort state lives in the view-model so it survives rescans; the ▲/▼ arrow is
    // driven reactively by the shared SortArrow binding on each header.
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null) return;
        var field = GetSortField(header.Column);
        if (string.IsNullOrEmpty(field)) return;   // non-sortable column or the padding header
        if (DataContext is ThingsViewModel vm) vm.SortBy(field);
    }
}
