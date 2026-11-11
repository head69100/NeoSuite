using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

/// <summary>Nettoyeur façon CCleaner : fichiers inutiles d'un côté, registre et raccourcis morts de l'autre.</summary>
public sealed partial class CleanerPage : Page
{
    private List<JunkCategory> _categories = [];
    private List<Leftover> _issues = [];
    private bool _filesAnalyzed;
    private bool _registryAnalyzed;
    private bool _busy;

    public CleanerPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // garde les résultats d'analyse en changeant de page
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_categories.Count > 0)
            return;
        _categories = JunkCleaner.GetCategories(AdminService.IsAdministrator);
        foreach (var c in _categories)
            c.PropertyChanged += (_, _) => UpdateTotals();
        JunkList.ItemsSource = _categories;
        UpdateTotals();
    }

    private bool IsFilesTab => (TabBar.SelectedItem?.Tag as string) != "Registry";

    private void TabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        JunkList.Visibility = IsFilesTab ? Visibility.Visible : Visibility.Collapsed;
        RegistryPanel.Visibility = IsFilesTab ? Visibility.Collapsed : Visibility.Visible;
        MessageBar.IsOpen = false;
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        if (IsFilesTab)
        {
            var size = _categories.Where(c => c.IsChecked && c.SizeBytes > 0).Sum(c => c.SizeBytes);
            TotalText.Text = !_filesAnalyzed ? "Cliquez sur « Analyser » pour voir l'espace récupérable"
                : size > 0 ? $"{InstalledProgram.FormatSize(size)} à libérer" : "Rien à nettoyer dans la sélection";
            CleanText.Text = "Nettoyer";
            CleanButton.IsEnabled = !_busy && _filesAnalyzed && size > 0;
        }
        else
        {
            var count = _issues.Count(i => i.IsChecked);
            TotalText.Text = !_registryAnalyzed ? "Cliquez sur « Analyser » pour chercher les entrées mortes"
                : _issues.Count == 0 ? "Aucun problème trouvé" : $"{count} sur {_issues.Count} problème(s) sélectionné(s)";
            CleanText.Text = "Corriger la sélection";
            CleanButton.IsEnabled = !_busy && count > 0;
        }
        AnalyzeButton.IsEnabled = !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TabBar.IsEnabled = !busy;
        UpdateTotals();
    }

    // ---------- Analyse ----------

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        MessageBar.IsOpen = false;
        try
        {
            if (IsFilesTab)
            {
                foreach (var category in _categories.Where(c => c.CanClean))
                {
                    category.Status = "Analyse…";
                    var (size, count) = await JunkCleaner.AnalyzeAsync(category);
                    category.Status = null;
                    category.FileCount = count;
                    category.SizeBytes = size;
                }
                _filesAnalyzed = true;
            }
            else
            {
                TotalText.Text = "Analyse du registre et des raccourcis…";
                _issues = await RegistryIssueScanner.ScanAsync(AdminService.IsAdministrator);
                foreach (var issue in _issues)
                    issue.PropertyChanged += (_, _) => UpdateTotals();
                IssueList.ItemsSource = _issues;
                _registryAnalyzed = true;
                if (_issues.Any(i => !i.CanDelete))
                    ShowMessage(InfoBarSeverity.Informational, "Certaines entrées demandent les droits administrateur",
                        "Elles sont grisées. Relancez NeoUninstaller en administrateur pour les corriger aussi.");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- Nettoyage ----------

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsFilesTab)
            await CleanFilesAsync();
        else
            await FixRegistryAsync();
    }

    private async Task CleanFilesAsync()
    {
        var selected = _categories.Where(c => c.IsChecked && c.CanClean && c.SizeBytes > 0).ToList();

        // Navigateurs/applis ouverts : leurs fichiers sont verrouillés, autant prévenir
        var running = selected.Where(JunkCleaner.IsLockedByRunningApp).Select(c => c.Name.Split(" · ")[0]).Distinct().ToList();
        var recycle = selected.Any(c => c.IsRecycleBin);
        if (running.Count > 0 || recycle)
        {
            var text = "";
            if (running.Count > 0)
                text += $"{string.Join(", ", running)} {(running.Count > 1 ? "sont ouverts" : "est ouvert")} : fermez-les pour tout nettoyer, sinon une partie de leur cache sera ignorée.\n\n";
            if (recycle)
                text += "La Corbeille sera vidée : son contenu sera supprimé définitivement.";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Avant de nettoyer",
                Content = new TextBlock { Text = text.Trim(), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Nettoyer",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        SetBusy(true);
        long freed = 0;
        int skipped = 0;
        try
        {
            foreach (var category in selected)
            {
                category.Status = "Nettoyage…";
                var result = await JunkCleaner.CleanAsync(category);
                freed += result.FreedBytes;
                skipped += result.Skipped;
                var (size, count) = await JunkCleaner.AnalyzeAsync(category);
                category.Status = null;
                category.FileCount = count;
                category.SizeBytes = size;
            }
        }
        finally
        {
            SetBusy(false);
        }

        ShowMessage(InfoBarSeverity.Success, $"{InstalledProgram.FormatSize(freed)} libérés",
            skipped == 0 ? "Nettoyage terminé." : $"{skipped} fichier(s) en cours d'utilisation ont été laissés (normal pour les fichiers ouverts).");
    }

    private async Task FixRegistryAsync()
    {
        var selected = _issues.Where(i => i.IsChecked && i.CanDelete).ToList();
        SetBusy(true);
        try
        {
            var report = await LeftoverCleaner.CleanAsync("Nettoyage du registre", selected);
            var fixedPaths = report.Items.Where(i => i.Success).Select(i => (i.Path, i.ValueName)).ToHashSet();
            _issues = _issues.Where(i => !fixedPaths.Contains((i.Path, i.ValueName))).ToList();
            IssueList.ItemsSource = _issues;
            if (report.Failed == 0)
                ShowMessage(InfoBarSeverity.Success, $"{report.Succeeded} problème(s) corrigé(s)", "Une sauvegarde a été créée : page Historique pour la restaurer.");
            else
                ShowMessage(InfoBarSeverity.Warning, $"{report.Succeeded} corrigé(s), {report.Failed} en échec",
                    string.Join("\n", report.Items.Where(i => !i.Success).Take(5).Select(i => i.Error)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
