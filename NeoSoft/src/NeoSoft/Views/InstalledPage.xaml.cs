using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoSoft.Models;
using NeoSoft.Services;

namespace NeoSoft.Views;

public sealed partial class InstalledPage : Page
{
    public InstalledPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        SoftwareManager.Changed += OnChanged;
        Refresh();
        if (SoftwareManager.LastRefresh is null)
            _ = SoftwareManager.RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => SoftwareManager.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        // Uniquement les mises à jour disponibles, par nom — la liste des programmes installés
        // est le rôle de NeoUninstaller, pas de NeoSoft
        var updates = SoftwareManager.Items
            .Where(i => i.HasUpdate || (i.IsBusy && i.RestingState == SoftwareState.UpdateAvailable))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        InstalledList.ItemsSource = updates;

        var refreshing = SoftwareManager.IsRefreshing;
        RefreshProgress.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !refreshing;
        EmptyPanel.Visibility = updates.Count == 0 && !refreshing && SoftwareManager.LastRefresh is not null ? Visibility.Visible : Visibility.Collapsed;

        UpdateAllButton.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllText.Text = $"Tout mettre à jour ({updates.Count})";

        SummaryText.Text = refreshing && SoftwareManager.LastRefresh is null ? "Recherche des mises à jour…"
            : updates.Count == 0 ? "Tout est à jour."
            : $"{updates.Count} mise{(updates.Count > 1 ? "s" : "")} à jour disponible{(updates.Count > 1 ? "s" : "")}"
              + (SoftwareManager.LastRefresh is { } last ? $" · vérifié à {last:HH:mm}" : "");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await SoftwareManager.RefreshAsync();

    private void UpdateAllButton_Click(object sender, RoutedEventArgs e) => SoftwareManager.EnqueueAllUpdates();

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SoftwareItem item)
            SoftwareManager.Enqueue(item, SoftwareOperation.Upgrade);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SoftwareItem item)
            SoftwareManager.Cancel(item);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("Catalog");
}
