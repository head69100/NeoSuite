using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NeoUninstaller.Views;

public sealed partial class ProgramsPage : Page
{
    public ProgramsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await LoadAsync(useCache: AppState.Programs.Count > 0 && !AppState.ListIsStale);
        AppState.ListIsStale = false;

        // Relancé en administrateur pour désinstaller des programmes précis (clés séparées par « ; »)
        if (AppState.PendingUninstallKey is { } keys)
        {
            AppState.PendingUninstallKey = null;
            var quiet = keys.StartsWith("quiet:");
            var programs = keys.Replace("quiet:", "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(k => AppState.Programs.FirstOrDefault(p => p.Key == k))
                .OfType<InstalledProgram>()
                .ToList();
            foreach (var p in programs)
                p.UseQuietUninstall = quiet;
            if (programs.Count > 0)
                App.MainWindow?.StartUninstall(programs);
        }
    }

    // ---------- Sélection multiple ----------

    private void MultiToggle_Click(object sender, RoutedEventArgs e)
    {
        var multi = MultiToggle.IsChecked == true;
        ProgramList.SelectionMode = multi ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        BatchBar.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        UpdateBatchBar();
    }

    private void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBatchBar();

    private void UpdateBatchBar()
    {
        if (MultiToggle.IsChecked != true)
            return;
        var count = ProgramList.SelectedItems.Count;
        BatchText.Text = count == 0 ? "Cochez les programmes à désinstaller" : $"{count} programme(s) sélectionné(s)";
        BatchButton.IsEnabled = count > 0;
    }

    private async void BatchButton_Click(object sender, RoutedEventArgs e)
    {
        var programs = ProgramList.SelectedItems.OfType<InstalledProgram>().ToList();
        if (programs.Count == 0)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Désinstaller {programs.Count} programmes ?",
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock
                {
                    Text = string.Join("\n", programs.Select(p => "• " + p.DisplayName)) +
                           "\n\nIls seront désinstallés l'un après l'autre, avec l'analyse de leurs restes à chaque fois.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Désinstaller",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RequestUninstallAsync(programs, quiet: QuietCheck.IsChecked == true);
    }

    private async Task LoadAsync(bool useCache)
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        if (!useCache)
            await AppState.RefreshAsync();
        else if (AppState.Programs.Count == 0)
            await AppState.RefreshAsync();

        ApplyFilter();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;

        foreach (var program in AppState.Programs.Where(p => p.Icon is null))
            _ = IconService.LoadAsync(program);
        _ = ComputeMissingSizesAsync();
        _ = DetectGhostsAsync();
        _ = LoadUsageAsync();
    }

    /// <summary>Programmes fantômes : encore dans la liste alors que leur désinstalleur et leur dossier ont disparu.</summary>
    private async Task DetectGhostsAsync()
    {
        var ghosts = await Task.Run(() => GhostPrograms.Find(AppState.Programs));
        _ghosts = ghosts;
        // Un message de succès (retrait effectué) reste affiché jusqu'à la prochaine actualisation manuelle
        if (ghosts.Count > 0 || GhostBar.Severity != InfoBarSeverity.Success)
            GhostBar.IsOpen = ghosts.Count > 0;
        if (ghosts.Count > 0)
        {
            GhostBar.Title = ghosts.Count == 1
                ? "1 programme fantôme dans la liste"
                : $"{ghosts.Count} programmes fantômes dans la liste";
            GhostBar.Message = string.Join(", ", ghosts.Take(3).Select(p => p.DisplayName)) + (ghosts.Count > 3 ? "…" : "")
                + " — leur désinstalleur n'existe plus.";
        }
    }

    private List<InstalledProgram> _ghosts = [];

    /// <summary>Dernière utilisation (dossier Prefetch) : lu en administrateur, sans cela le tri reste vide.</summary>
    private async Task LoadUsageAsync()
    {
        await UsageService.LoadAsync(AppState.Programs);
        if (SortCombo.SelectedIndex is 4 or 5)
            ApplyFilter();
    }

    private async void RemoveGhosts_Click(object sender, RoutedEventArgs e)
    {
        if (_ghosts.Count == 0)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Retirer {_ghosts.Count} programme(s) fantôme(s) ?",
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock
                {
                    Text = string.Join("\n", _ghosts.Select(p => "• " + p.DisplayName)) +
                           "\n\nIls seront retirés de la liste Windows (clé de registre sauvegardée avant, restaurable depuis l'Historique). " +
                           "Ils ne sont de toute façon plus désinstallables normalement.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Retirer de la liste",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var report = await GhostPrograms.RemoveAsync(_ghosts, AdminService.IsAdministrator);
        await LoadAsync(useCache: false);
        GhostBar.Severity = report.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        GhostBar.Title = report.Failed == 0
            ? $"{report.Succeeded} entrée(s) retirée(s) de la liste"
            : $"{report.Succeeded} retirée(s), {report.Failed} en échec (droits administrateur requis)";
        GhostBar.Message = $"Sauvegarde : {report.BackupFolder}";
        GhostBar.IsOpen = true;
    }

    /// <summary>Taille réelle du dossier pour les programmes qui ne la déclarent pas.</summary>
    private async Task ComputeMissingSizesAsync()
    {
        foreach (var program in AppState.Programs.Where(p => p.SizeBytes is null).ToList())
        {
            var dir = LeftoverScanner.ResolveInstallDir(program);
            if (dir is null || !Directory.Exists(dir))
                continue;
            var size = await Task.Run(() =>
            {
                long total = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        total += f.Length;
                }
                catch (Exception)
                {
                }
                return total;
            });
            if (size > 0)
                program.SizeBytes = size;
        }
        UpdateSummary();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<InstalledProgram> list = AppState.Programs;
        if (query.Length > 0)
        {
            list = list.Where(p => p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                || (p.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        list = SortCombo.SelectedIndex switch
        {
            1 => list.OrderByDescending(p => p.SizeBytes ?? 0),
            2 => list.OrderByDescending(p => p.InstallDate ?? DateTime.MinValue),
            3 => list.OrderBy(p => p.PublisherText, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            4 => list.OrderByDescending(p => p.LastUsed ?? DateTime.MinValue),
            5 => list.Where(p => p.IsUnused).OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };

        var items = list.ToList();
        ProgramList.ItemsSource = items;
        ProgramList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = items.Count == 0 && AppState.Programs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _visibleCount = items.Count;
        UpdateSummary();
    }

    private int _visibleCount;

    private void UpdateSummary()
    {
        var total = AppState.Programs.Sum(p => p.SizeBytes ?? 0);
        SummaryText.Text = $"{AppState.Programs.Count} programmes · {InstalledProgram.FormatSize(total)} au total"
            + (_visibleCount != AppState.Programs.Count ? $" · {_visibleCount} affichés" : "");
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgramList is not null)
            ApplyFilter();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync(useCache: false);

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is InstalledProgram program)
            await RequestUninstallAsync(program);
    }

    private async void ProgramList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ProgramList.SelectedItem is InstalledProgram program)
            await RequestUninstallAsync(program);
    }

    private void Program_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledProgram program } row)
            return;
        e.Handled = true;
        ProgramList.SelectedItem = program;

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Désinstaller", "\uE74D", async () => await RequestUninstallAsync(program)));
        if (program.HasQuietUninstall)
            menu.Items.Add(Item("D\u00E9sinstallation silencieuse", "\uE767", async () => await RequestUninstallAsync([program], quiet: true)));
        menu.Items.Add(Item("Nettoyer les restes uniquement", "\uE7BA", async () => await RequestUninstallAsync(program, scanOnly: true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var dir = LeftoverScanner.ResolveInstallDir(program);
        if (dir is not null && Directory.Exists(dir))
            menu.Items.Add(Item("Ouvrir le dossier d'installation", "\uE838", () => Process.Start("explorer.exe", $"\"{dir}\"")));
        menu.Items.Add(Item("Rechercher sur le web", "\uE774", () =>
            Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(program.DisplayName)}") { UseShellExecute = true })));
        menu.Items.Add(Item("Copier le nom", "\uE8C8", () =>
        {
            var package = new DataPackage();
            package.SetText(program.DisplayName);
            Clipboard.SetContent(package);
        }));

        if (e.TryGetPosition(row, out var point))
            menu.ShowAt(row, point);
        else
            menu.ShowAt(row);
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private async void ForcedButton_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Nom du programme", PlaceholderText = "Ex. : MonLogiciel" };
        var folder = new TextBox { Header = "Dossier d'installation (facultatif)", PlaceholderText = @"Ex. : C:\Program Files\MonLogiciel" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Désinstallation forcée",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Pour un programme absent de la liste ou dont le désinstalleur ne marche plus : NeoUninstaller cherche ses restes " +
                               "(dossiers, registre, raccourcis) d'après son nom et son dossier.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    name,
                    folder,
                },
            },
            PrimaryButtonText = "Analyser",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text))
            return;

        var forced = new InstalledProgram
        {
            Hive = Microsoft.Win32.RegistryHive.CurrentUser,
            View = Microsoft.Win32.RegistryView.Registry64,
            SubKeyName = "forced:" + name.Text.Trim(),
            DisplayName = name.Text.Trim(),
            InstallLocation = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim().Trim('"').TrimEnd('\\'),
            IsForced = true,
        };
        App.MainWindow?.StartUninstall(forced);
    }

    /// <summary>Programme installé pour tous les utilisateurs : on propose de passer en administrateur d'abord.</summary>
    private Task RequestUninstallAsync(InstalledProgram program, bool scanOnly = false) =>
        scanOnly ? RequestScanOnlyAsync(program) : RequestUninstallAsync([program], quiet: false);

    /// <summary>Un ou plusieurs programmes ; ceux installés pour tous demandent de passer en administrateur d'abord.</summary>
    private async Task RequestUninstallAsync(List<InstalledProgram> programs, bool quiet)
    {
        if (!await EnsureAdminIfNeededAsync(programs, quiet))
            return;
        foreach (var p in programs)
            p.UseQuietUninstall = quiet && p.HasQuietUninstall;
        App.MainWindow?.StartUninstall(programs.Count == 1 ? programs[0] : programs);
    }

    /// <summary>Renvoie false si l'utilisateur annule ou si NeoUninstaller redémarre en administrateur.</summary>
    private async Task<bool> EnsureAdminIfNeededAsync(List<InstalledProgram> programs, bool quiet)
    {
        var machineWide = programs.Where(p => !p.IsPerUser).ToList();
        if (machineWide.Count == 0 || AdminService.IsAdministrator)
            return true;

        var names = machineWide.Count == 1 ? $"{machineWide[0].DisplayName} est installé" : $"{machineWide.Count} de ces programmes sont installés";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Droits administrateur recommandés",
            Content = new TextBlock
            {
                Text = $"{names} pour tous les utilisateurs. Pour supprimer aussi leurs restes dans Program Files " +
                       "et le registre système, NeoUninstaller doit redémarrer en administrateur (Windows vous demandera confirmation).",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Redémarrer en administrateur",
            SecondaryButtonText = "Continuer sans",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
            return false;
        if (choice == ContentDialogResult.Secondary)
            return true;

        var keys = (quiet ? "quiet:" : "") + string.Join(';', programs.Select(p => p.Key));
        if (AdminService.RelaunchElevated($"--uninstall=\"{keys}\""))
            Application.Current.Exit();
        return false;
    }

    private async Task RequestScanOnlyAsync(InstalledProgram program)
    {
        if (!await EnsureAdminIfNeededAsync([program], quiet: false))
            return;
        {
            // Analyse des restes sans lancer le désinstalleur
            var copy = new InstalledProgram
            {
                Hive = program.Hive,
                View = program.View,
                SubKeyName = program.SubKeyName,
                DisplayName = program.DisplayName,
                Publisher = program.Publisher,
                Version = program.Version,
                InstallLocation = program.InstallLocation,
                DisplayIcon = program.DisplayIcon,
                UninstallString = program.UninstallString,
                IsForced = true,
                Icon = program.Icon,
            };
            App.MainWindow?.StartUninstall(copy);
        }
    }
}
