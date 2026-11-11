using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeoDriver.Models;
using NeoDriver.Services;

namespace NeoDriver.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var entries = HistoryService.Load();
        HistoryList.ItemsSource = entries;
        HistoryList.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var ok = entries.Count(e => e.Success);
        SummaryText.Text = entries.Count == 0 ? "" : $"{ok} réussie(s), {entries.Count - ok} en échec";
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryEntry entry)
            return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Revenir à l'ancien pilote ?",
            Content = new TextBlock
            {
                Text = $"Le pilote de « {entry.DeviceName} » installé le {entry.DateText} sera retiré ; Windows reprendra la version {entry.PreviousVersion} si elle est encore disponible. " +
                       "Un point de restauration est créé avant (si l'option est activée).",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Revenir en arrière",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (sender is Button button)
            button.IsEnabled = false;
        ResultBar.IsOpen = false;
        try
        {
            var result = await DriverRollbackService.RollbackAsync(entry, AppSettings.Current.CreateRestorePoint);
            if (result.Success)
            {
                // L'historique est relu depuis le fichier : on retrouve l'entrée par sa date et son périphérique
                var all = HistoryService.Load();
                foreach (var saved in all.Where(s => s.Date == entry.Date && s.DeviceName == entry.DeviceName))
                    saved.RolledBack = true;
                HistoryService.Save(all);
                Reload();
            }
            ResultBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Title = result.Success ? "Retour en arrière effectué" : "Retour en arrière impossible";
            ResultBar.Message = result.Message;
        }
        catch (Exception ex)
        {
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Title = "Retour en arrière impossible";
            ResultBar.Message = ex.Message;
        }
        ResultBar.IsOpen = true;
        if (sender is Button b)
            b.IsEnabled = true;
    }
}
