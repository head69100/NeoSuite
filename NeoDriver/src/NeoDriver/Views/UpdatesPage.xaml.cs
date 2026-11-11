using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoDriver.Models;
using NeoDriver.Services;

namespace NeoDriver.Views;

public sealed partial class UpdatesPage : Page
{
    private static bool _installing;

    private ObservableCollection<DriverItem> _items = [];

    public UpdatesPage()
    {
        InitializeComponent();
        // Garde la page en mémoire : une installation en cours reste visible au retour
        NavigationCacheMode = NavigationCacheMode.Required;
        DriverList.ItemsSource = _items;
        AdminBar.IsOpen = !AdminService.IsAdministrator;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_installing)
            return;

        if (DriverUpdateService.IsSearching || DriverUpdateService.LastResult is null)
            await SearchAsync();
        else
            ShowResult(DriverUpdateService.LastResult);

        // Relancé en administrateur pour installer des pilotes précis
        if (App.PendingInstallIds is { } ids)
        {
            App.PendingInstallIds = null;
            var toInstall = _items.Where(i => ids.Contains(i.UpdateId)).ToList();
            if (toInstall.Count > 0)
                await InstallAsync(toInstall);
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_items.Where(i => i.ShowButton).ToList());

    private async void UpdateOneButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriverItem item)
            await InstallAsync([item]);
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => AdminService.RestartComputer();

    // ---------- Masquer une mise à jour ----------

    private async void HideOneButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriverItem item)
            return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Ne plus proposer ce pilote ?",
            Content = new TextBlock
            {
                Text = $"« {item.DeviceName} » ne sera plus proposé par NeoDriver ni par Windows Update. Utile si cette mise à jour a déjà posé problème. Le bouton « Masqués » permet de la réafficher.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Ne plus proposer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await HiddenDriverService.HideAsync(item);
        }
        catch (Exception ex)
        {
            ErrorBar.Title = "Impossible de masquer cette mise à jour";
            ErrorBar.Message = ex.HResult == unchecked((int)0x80070005)
                ? "Windows exige les droits administrateur pour masquer une mise à jour : redémarrez NeoDriver en administrateur."
                : ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }

        DriverUpdateService.RemoveInstalled([item]);
        ShowResult(DriverUpdateService.LastResult ?? []);
    }

    private async void HiddenButton_Click(object sender, RoutedEventArgs e)
    {
        List<DriverItem> hidden;
        try
        {
            hidden = await HiddenDriverService.GetHiddenAsync();
        }
        catch (Exception ex)
        {
            ErrorBar.Title = "Liste des pilotes masqués indisponible";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }

        var shownAgain = false;
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        if (hidden.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Aucune mise à jour n'est masquée.", TextWrapping = TextWrapping.Wrap });

        foreach (var item in hidden)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.DeviceName, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = item.Manufacturer + (item.NewDate is { } d ? $" · {d:dd/MM/yyyy}" : ""),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            var button = new Button { Content = "Réafficher", VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    await HiddenDriverService.ShowAgainAsync(item);
                    shownAgain = true;
                    button.Content = "Réaffiché";
                }
                catch (Exception ex)
                {
                    button.IsEnabled = true;
                    ErrorBar.Title = "Impossible de réafficher cette mise à jour";
                    ErrorBar.Message = ex.Message;
                    ErrorBar.IsOpen = true;
                }
            };
            row.Children.Add(text);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            panel.Children.Add(row);
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Mises à jour masquées",
            Content = new ScrollViewer { MaxHeight = 380, Content = panel },
            CloseButtonText = "Fermer",
        }.ShowAsync();

        if (shownAgain)
            await SearchAsync();
    }

    private async Task SearchAsync()
    {
        SearchButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;
        ErrorBar.IsOpen = false;
        ResultBar.IsOpen = false;
        DriverList.Visibility = Visibility.Collapsed;
        UpToDatePanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        SummaryText.Text = "Recherche en cours…";

        try
        {
            ShowResult(await DriverUpdateService.SearchAsync());
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            SummaryText.Text = "";
            ErrorBar.Title = "Recherche impossible";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            SearchButton.IsEnabled = !_installing;
        }
    }

    private void ShowResult(IReadOnlyList<DriverItem> items)
    {
        _items = new ObservableCollection<DriverItem>(items);
        DriverList.ItemsSource = _items;
        LoadingPanel.Visibility = Visibility.Collapsed;
        DriverList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpToDatePanel.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAllButton.IsEnabled = !_installing && items.Any(i => i.ShowButton);

        var when = DriverUpdateService.LastSearch is { } t ? $" · vérifié à {t:HH:mm}" : "";
        SummaryText.Text = items.Count switch
        {
            0 => $"Aucune mise à jour disponible{when}",
            1 => $"1 mise à jour disponible{when}",
            var n => $"{n} mises à jour disponibles{when}",
        };
        App.MainWindow?.SetUpdatesBadge(items.Count);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        SearchButton.IsEnabled = enabled;
        UpdateAllButton.IsEnabled = enabled && _items.Any(i => i.ShowButton);
    }

    private async Task InstallAsync(IReadOnlyList<DriverItem> items)
    {
        if (items.Count == 0 || _installing)
            return;

        if (!AdminService.IsAdministrator)
        {
            await RequestElevationAsync(items);
            return;
        }

        // Licences éventuelles à accepter par l'utilisateur
        foreach (var (item, text) in await DriverInstallService.GetPendingEulasAsync(items))
        {
            var accepted = await ShowDialogAsync(
                $"Licence : {item.DeviceName}",
                new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } },
                "Accepter", "Refuser");
            if (!accepted)
                return;
            await DriverInstallService.AcceptEulaAsync(item);
        }

        _installing = true;
        SetButtonsEnabled(false);
        ResultBar.IsOpen = false;
        ErrorBar.IsOpen = false;
        ProgressBarInfo.IsOpen = true;

        try
        {
            string? restoreNote = null;
            if (AppSettings.Current.CreateRestorePoint)
            {
                ProgressBarInfo.Message = "Création d'un point de restauration…";
                try
                {
                    var created = await RestorePointService.CreateAsync("NeoDriver - mise à jour de pilotes");
                    if (!created)
                        restoreNote = "Windows n'a pas créé de nouveau point de restauration : il en existe déjà un de moins de 24 h.";
                }
                catch (Exception ex)
                {
                    var proceed = await ShowDialogAsync(
                        "Point de restauration impossible",
                        new TextBlock { Text = $"{ex.Message}\n\nVoulez-vous installer les pilotes sans point de restauration ?", TextWrapping = TextWrapping.Wrap },
                        "Continuer sans", "Annuler");
                    if (!proceed)
                        return;
                    restoreNote = "Aucun point de restauration n'a été créé.";
                }
            }

            ProgressBarInfo.Message = items.Count == 1
                ? $"Téléchargement et installation de {items[0].DeviceName}…"
                : $"Téléchargement et installation de {items.Count} pilotes…";

            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Error is not null)
                    p.Item.ErrorMessage = p.Error;
                p.Item.State = p.State;
            });

            var summary = await DriverInstallService.InstallAsync(items, progress);

            HistoryService.Add(summary.Outcomes.Select(o => new HistoryEntry
            {
                Date = DateTime.Now,
                DeviceName = o.Item.DeviceName,
                Category = o.Item.Category,
                Title = o.Item.Title,
                PreviousVersion = o.Item.IsMissing ? null : o.Item.CurrentVersion,
                Success = o.Success,
                Message = o.Message,
            }));

            DriverUpdateService.RemoveInstalled(summary.Outcomes.Where(o => o.Success).Select(o => o.Item));
            App.MainWindow?.SetUpdatesBadge(DriverUpdateService.LastResult?.Count ?? 0);
            ShowSummary(summary, restoreNote);
        }
        catch (Exception ex)
        {
            foreach (var item in items.Where(i => i.IsBusy))
            {
                item.ErrorMessage = "Annulé";
                item.State = DriverState.Failed;
            }
            ErrorBar.Title = "Installation impossible";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _installing = false;
            ProgressBarInfo.IsOpen = false;
            SetButtonsEnabled(true);
        }
    }

    private void ShowSummary(InstallSummary summary, string? restoreNote)
    {
        ResultBar.Severity = summary.Failed == 0 ? InfoBarSeverity.Success
            : summary.Succeeded == 0 ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;

        ResultBar.Title = (summary.Succeeded, summary.Failed) switch
        {
            (_, 0) => summary.Succeeded == 1 ? "Pilote installé" : $"{summary.Succeeded} pilotes installés",
            (0, _) => "L'installation a échoué",
            _ => $"{summary.Succeeded} installé(s), {summary.Failed} en échec",
        };

        var lines = new List<string>();
        if (summary.RebootRequired)
            lines.Add("Redémarrez le PC pour terminer l'installation.");
        if (restoreNote is not null)
            lines.Add(restoreNote);
        ResultBar.Message = string.Join(" ", lines);

        RestartButton.Visibility = summary.RebootRequired ? Visibility.Visible : Visibility.Collapsed;
        ResultBar.IsOpen = true;
    }

    private async Task RequestElevationAsync(IReadOnlyList<DriverItem> items)
    {
        var remember = new CheckBox
        {
            Content = "Toujours démarrer NeoDriver en administrateur (ne plus demander)",
            IsChecked = true,
        };
        var restart = await ShowDialogAsync(
            "Droits administrateur requis",
            new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "L'installation de pilotes nécessite les droits administrateur.\n\n" +
                               "NeoDriver va redémarrer en mode administrateur (Windows vous demandera confirmation), " +
                               "puis lancera l'installation automatiquement.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    remember,
                },
            },
            "Redémarrer en administrateur", "Annuler");
        if (!restart)
            return;

        var ids = string.Join(",", items.Select(i => i.UpdateId));
        var autoAdmin = remember.IsChecked == true ? " --enable-auto-admin" : "";
        if (AdminService.RelaunchElevated($"--page=Updates --install={ids}{autoAdmin}"))
            App.MainWindow?.Quit();
        else
        {
            ErrorBar.Title = "Élévation refusée";
            ErrorBar.Message = "Sans les droits administrateur, NeoDriver ne peut pas installer de pilotes.";
            ErrorBar.IsOpen = true;
        }
    }

    private async Task<bool> ShowDialogAsync(string title, object content, string primary, string close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
