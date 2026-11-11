using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoSoft.Services;

namespace NeoSoft.Views;

public sealed partial class ActivityPage : Page
{
    public ActivityPage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = SoftwareManager.Activity;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        SoftwareManager.Changed += OnChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => SoftwareManager.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        // L'opération en cours d'abord, puis la file d'attente
        var pending = SoftwareManager.Items.Where(i => i.IsBusy).OrderBy(i => i.State == Models.SoftwareState.Queued).ToList();
        PendingList.ItemsSource = pending;
        NoPendingText.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelAllButton.Visibility = pending.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var hasHistory = SoftwareManager.Activity.Count > 0;
        NoHistoryText.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Models.SoftwareItem item)
            SoftwareManager.Cancel(item);
    }

    private void CancelAllButton_Click(object sender, RoutedEventArgs e) => SoftwareManager.CancelAll();

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e) => SoftwareManager.ClearHistory();
}
