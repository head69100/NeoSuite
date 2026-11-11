using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

public sealed partial class WindowsAppsPage : Page
{
    private List<WindowsApp> _apps = [];
    private bool _removing;

    public WindowsAppsPage()
    {
        InitializeComponent();
        AllUsersCheck.IsEnabled = AdminService.IsAdministrator;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        try
        {
            _apps = await WindowsAppsService.LoadAsync();
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Liste indisponible", ex.Message);
            _apps = [];
        }

        foreach (var app in _apps)
        {
            if (app.LogoUri is not null)
                app.Logo = new BitmapImage(app.LogoUri);
            app.PropertyChanged += (_, _) => UpdateSelection();
        }
        ApplyFilter();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<WindowsApp> list = FilterCombo.SelectedIndex switch
        {
            1 => _apps.Where(a => a.IsCommonBloat),
            2 => _apps.Where(a => a.IsMicrosoft),
            3 => _apps.Where(a => !a.IsMicrosoft),
            _ => _apps,
        };
        if (query.Length > 0)
            list = list.Where(a => a.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                || a.IdentityName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        AppList.ItemsSource = list.ToList();
        AppList.Visibility = Visibility.Visible;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _apps.Count(a => a.IsChecked);
        SelectionText.Text = selected == 0 ? $"{_apps.Count} applis Windows" : $"{selected} appli(s) sélectionnée(s) sur {_apps.Count}";
        RemoveButton.IsEnabled = selected > 0 && !_removing;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppList is not null)
            ApplyFilter();
    }

    private void CheckBloat_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in _apps.Where(a => a.IsCommonBloat))
            app.IsChecked = true;
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _apps.Where(a => a.IsChecked).ToList();
        if (selected.Count == 0)
            return;

        var essential = selected.Where(a => a.IsEssential).ToList();
        var warning = essential.Count == 0 ? "" :
            $"\n\n⚠ Attention : {string.Join(", ", essential.Select(a => a.DisplayName))} sont essentielles au bon fonctionnement de Windows " +
            "(Microsoft Store, installation d'applis, sécurité ou lecture de médias). Les supprimer est déconseillé.";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = selected.Count == 1 ? $"Supprimer {selected[0].DisplayName} ?" : $"Supprimer {selected.Count} applis Windows ?",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = string.Join("\n", selected.Select(a => "• " + a.DisplayName)) +
                           "\n\nElles seront retirées " + (AllUsersCheck.IsChecked == true ? "pour tous les utilisateurs du PC." : "pour votre compte.") +
                           " Vous pourrez les réinstaller depuis le Microsoft Store." + warning,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _removing = true;
        UpdateSelection();
        var failures = new List<string>();
        var done = 0;
        foreach (var app in selected)
        {
            ShowMessage(InfoBarSeverity.Informational, "Suppression en cours…", $"{app.DisplayName} ({++done}/{selected.Count})");
            try
            {
                await WindowsAppsService.RemoveAsync(app, AllUsersCheck.IsChecked == true);
                _apps.Remove(app);
            }
            catch (Exception ex)
            {
                failures.Add($"{app.DisplayName} : {ex.Message}");
            }
        }
        _removing = false;
        ApplyFilter();

        if (failures.Count == 0)
            ShowMessage(InfoBarSeverity.Success, "Applis supprimées", $"{selected.Count} appli(s) retirée(s).");
        else
            ShowMessage(InfoBarSeverity.Warning, $"{selected.Count - failures.Count} supprimée(s), {failures.Count} en échec", string.Join("\n", failures));
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
