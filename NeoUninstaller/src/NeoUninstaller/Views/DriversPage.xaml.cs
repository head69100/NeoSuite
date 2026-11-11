using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

/// <summary>
/// Pilotes orphelins du magasin Windows (pnputil) : supprimables en administrateur,
/// uniquement s'ils ne sont plus utilisés par aucun appareil.
/// </summary>
public sealed partial class DriversPage : Page
{
    private List<DriverPackage> _packages = [];

    public DriversPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        DriverList.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ResultBar.IsOpen = false;

        _packages = await OrphanDrivers.FindAsync();

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        DriverList.ItemsSource = _packages;
        DriverList.Visibility = _packages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _packages.Count == 0
            ? "Tous les pilotes installés sont utilisés."
            : $"{_packages.Count} paquet(s) de pilotes inutilisés" + (AdminService.IsAdministrator ? "" : " · lancement en administrateur pour les supprimer");
        UpdateButtons();
    }

    private void DriverList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void CheckAllButton_Click(object sender, RoutedEventArgs e)
    {
        DriverList.SelectAll();
        UpdateButtons();
    }

    private void UncheckAllButton_Click(object sender, RoutedEventArgs e)
    {
        // ListView n'a pas de « deselect all » : repasser en mode None vide la sélection
        DriverList.SelectionMode = ListViewSelectionMode.None;
        DriverList.SelectionMode = ListViewSelectionMode.Multiple;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var count = DriverList?.SelectedItems.Count ?? 0;
        var total = _packages.Count;
        DeleteButton.IsEnabled = count > 0;
        DeleteLabel.Text = count == 0
            ? "Supprimer la sélection"
            : $"Supprimer la sélection ({count})";
        if (CheckAllButton is not null)
        {
            CheckAllButton.IsEnabled = total > 0 && count < total;
            UncheckAllButton.IsEnabled = count > 0;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = DriverList.SelectedItems.OfType<DriverPackage>().ToList();
        if (selected.Count == 0)
            return;

        // Suppression de paquets : droits administrateur obligatoires
        if (!AdminService.IsAdministrator)
        {
            var relaunch = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Droits administrateur requis",
                Content = new TextBlock
                {
                    Text = "La suppression des pilotes du magasin Windows demande les droits administrateur. NeoUninstaller va redémarrer en administrateur (Windows vous demandera confirmation).",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Redémarrer en administrateur",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await relaunch.ShowAsync() == ContentDialogResult.Primary
                && AdminService.RelaunchElevated("--page=Drivers"))
                Application.Current.Exit();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Supprimer {selected.Count} paquet(s) de pilotes ?",
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock
                {
                    Text = string.Join("\n", selected.Select(p => $"• {p.PublishedName} — {p.Description}")) +
                           "\n\nWindows les retire du magasin. Un pilote encore utilisé par un appareil sera refusé (c'est une sécurité).",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        DeleteButton.IsEnabled = false;
        var ok = 0;
        var failed = new List<string>();
        foreach (var package in selected)
        {
            ResultBar.Severity = InfoBarSeverity.Informational;
            ResultBar.Title = "Suppression en cours";
            ResultBar.Message = $"{package.PublishedName} — {package.Provider}";
            ResultBar.IsOpen = true;

            var (success, message) = await OrphanDrivers.DeleteAsync(package);
            if (success)
                ok++;
            else
                failed.Add($"{package.PublishedName} : {message.Split('\n').FirstOrDefault()?.Trim()}");
        }

        ResultBar.Severity = failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title = failed.Count == 0 ? $"{ok} pilote(s) supprimé(s)" : $"{ok} supprimé(s), {failed.Count} en échec";
        ResultBar.Message = string.Join("\n", failed);
        ResultBar.IsOpen = true;

        await LoadAsync();
    }
}
