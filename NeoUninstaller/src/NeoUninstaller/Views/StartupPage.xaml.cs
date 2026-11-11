using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

public sealed partial class StartupPage : Page
{
    private List<StartupItem> _items = [];

    public StartupPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        _items = await StartupManager.LoadAsync();
        ApplyFilter();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        foreach (var item in _items.Where(i => i.ExecutablePath is not null))
            _ = LoadIconAsync(item);
    }

    private static async Task LoadIconAsync(StartupItem item)
    {
        if (await IconService.GetAsync(item.ExecutablePath!) is { } icon)
            item.Icon = icon;
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var visible = query.Length == 0 ? _items : _items.Where(i =>
            i.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || i.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (i.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
        ItemList.ItemsSource = visible;
        ItemList.Visibility = Visibility.Visible;

        var enabled = _items.Count(i => i.IsEnabled);
        SummaryText.Text = $"{_items.Count} programmes se lancent avec Windows, dont {enabled} activés. " +
                           "Désactiver un programme accélère le démarrage sans le désinstaller.";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: StartupItem item } toggle || toggle.IsOn == item.IsEnabled)
            return; // changement venu de la liaison, pas de l'utilisateur

        if (item.RequiresAdmin && !AdminService.IsAdministrator)
        {
            toggle.IsOn = item.IsEnabled;
            ShowAdminNeeded(item);
            return;
        }

        try
        {
            await StartupManager.SetEnabledAsync(item, toggle.IsOn);
            item.IsEnabled = toggle.IsOn;
            ApplySummaryOnly();
        }
        catch (Exception ex)
        {
            toggle.IsOn = item.IsEnabled;
            ShowMessage(InfoBarSeverity.Error, "Modification impossible", ex is UnauthorizedAccessException
                ? "Accès refusé : cet élément demande les droits administrateur."
                : ex.Message);
        }
    }

    private void ApplySummaryOnly()
    {
        var enabled = _items.Count(i => i.IsEnabled);
        SummaryText.Text = $"{_items.Count} programmes se lancent avec Windows, dont {enabled} activés. " +
                           "Désactiver un programme accélère le démarrage sans le désinstaller.";
    }

    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupItem item } row)
            return;
        e.Handled = true;

        var menu = new MenuFlyout();
        var toggle = new MenuFlyoutItem { Text = item.IsEnabled ? "Désactiver" : "Activer", Icon = new FontIcon { Glyph = item.IsEnabled ? "" : "" } };
        toggle.Click += async (_, _) => await ToggleFromMenuAsync(item);
        menu.Items.Add(toggle);

        if (item.ExecutablePath is not null)
        {
            var open = new MenuFlyoutItem { Text = "Ouvrir l'emplacement du fichier", Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{item.ExecutablePath}\"");
            menu.Items.Add(open);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Supprimer du démarrage", Icon = new FontIcon { Glyph = "" } };
        delete.Click += async (_, _) => await DeleteAsync(item);
        menu.Items.Add(delete);

        if (e.TryGetPosition(row, out var point))
            menu.ShowAt(row, point);
        else
            menu.ShowAt(row);
    }

    private async Task ToggleFromMenuAsync(StartupItem item)
    {
        if (item.RequiresAdmin && !AdminService.IsAdministrator)
        {
            ShowAdminNeeded(item);
            return;
        }
        try
        {
            await StartupManager.SetEnabledAsync(item, !item.IsEnabled);
            item.IsEnabled = !item.IsEnabled;
            ApplySummaryOnly();
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Modification impossible", ex.Message);
        }
    }

    private async Task DeleteAsync(StartupItem item)
    {
        if (item.RequiresAdmin && !AdminService.IsAdministrator)
        {
            ShowAdminNeeded(item);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Supprimer « {item.DisplayName} » du démarrage ?",
            Content = new TextBlock
            {
                Text = "L'entrée est retirée définitivement (une sauvegarde est gardée dans l'Historique). " +
                       "Pour simplement l'empêcher de se lancer, mieux vaut la désactiver.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var report = await StartupManager.DeleteAsync(item);
            if (report is { Failed: > 0 })
                throw new InvalidOperationException(report.Items.First(i => !i.Success).Error);
            _items.Remove(item);
            ApplyFilter();
            ShowMessage(InfoBarSeverity.Success, "Supprimé du démarrage", $"{item.DisplayName} ne se lancera plus avec Windows.");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Suppression impossible", ex.Message);
        }
    }

    private void ShowAdminNeeded(StartupItem item)
    {
        var relaunch = new Button { Content = "Redémarrer en administrateur" };
        relaunch.Click += (_, _) =>
        {
            if (AdminService.RelaunchElevated("--page=Startup"))
                Application.Current.Exit();
        };
        MessageBar.ActionButton = relaunch;
        ShowMessage(InfoBarSeverity.Warning, "Droits administrateur requis",
            $"« {item.DisplayName} » est lancé pour tous les utilisateurs ({item.SourceText}) : NeoUninstaller doit être en administrateur pour le modifier.",
            keepButton: true);
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message, bool keepButton = false)
    {
        if (!keepButton)
            MessageBar.ActionButton = null;
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
