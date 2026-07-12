using System.Globalization;
using System.Windows;
using OpenWire.Core.Models;

namespace OpenWire.App.Views;

/// <summary>Small editor for a per-app data quota. Result is read after ShowDialog():
/// <see cref="Result"/> Save with <see cref="Quota"/>, Remove, or Cancel.</summary>
public partial class QuotaDialog : Window
{
    public enum DialogResultKind { Cancel, Save, Remove }

    public DialogResultKind Result { get; private set; } = DialogResultKind.Cancel;
    public AppQuota? Quota { get; private set; }

    private readonly string _appId;
    private readonly string _appName;
    private readonly string _exePath;

    public QuotaDialog(string appId, string appName, string exePath, AppQuota? existing)
    {
        InitializeComponent();
        _appId = appId;
        _appName = appName;
        _exePath = exePath;
        AppNameText.Text = string.IsNullOrWhiteSpace(appName) ? appId : appName;
        RemoveButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;

        if (existing is not null)
        {
            // Present the stored byte limit in the larger of MB/GB that keeps it a clean number.
            if (existing.LimitBytes % (1024L * 1024 * 1024) == 0)
            {
                UnitGb.IsChecked = true;
                LimitBox.Text = (existing.LimitBytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                UnitMb.IsChecked = true;
                LimitBox.Text = Math.Max(1, existing.LimitBytes / (1024L * 1024)).ToString(CultureInfo.InvariantCulture);
            }
            PeriodDaily.IsChecked = existing.Period == QuotaPeriod.Daily;
            PeriodWeekly.IsChecked = existing.Period == QuotaPeriod.Weekly;
            PeriodMonthly.IsChecked = existing.Period == QuotaPeriod.Monthly;
            AutoBlockBox.IsChecked = existing.AutoBlock;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LimitBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || value <= 0)
        {
            LimitBox.Focus();
            LimitBox.SelectAll();
            return;
        }

        long unit = UnitGb.IsChecked == true ? 1024L * 1024 * 1024 : 1024L * 1024;
        long limitBytes = (long)Math.Round(value * unit);
        if (limitBytes <= 0) return;

        var period = PeriodDaily.IsChecked == true ? QuotaPeriod.Daily
            : PeriodWeekly.IsChecked == true ? QuotaPeriod.Weekly
            : QuotaPeriod.Monthly;

        Quota = new AppQuota
        {
            AppId = _appId,
            ExecutablePath = _exePath,
            AppName = _appName,
            LimitBytes = limitBytes,
            Period = period,
            AutoBlock = AutoBlockBox.IsChecked == true,
        };
        Result = DialogResultKind.Save;
        Close();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        Result = DialogResultKind.Remove;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = DialogResultKind.Cancel;
        Close();
    }
}
