using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoDriver.Models;
using NeoDriver.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeoDriver.Views;

public sealed partial class InstallPage : Page
{
    /// <summary>Demandé par le tableau de bord : lance la détection dès l'ouverture de la page.</summary>
    public static bool DetectOnOpen { get; set; }

    private string? _infPath;
    private string? _folderPath;
    private bool _busy;

    public InstallPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (DetectOnOpen)
        {
            DetectOnOpen = false;
            await DetectAsync();
        }
    }

    // ---------- Choix du fichier ou du dossier ----------

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".inf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        _infPath = file.Path;
        SelectedFileText.Text = file.Path;
        InstallFileButton.IsEnabled = !_busy;
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        var count = await DriverPackageService.CountInfFilesAsync(folder.Path);
        _folderPath = count > 0 ? folder.Path : null;
        SelectedFolderText.Text = count > 0
            ? $"{folder.Path} · {count} fichier{(count > 1 ? "s" : "")} .inf trouvé{(count > 1 ? "s" : "")}"
            : $"{folder.Path} · aucun fichier .inf dans ce dossier";
        InstallFolderButton.IsEnabled = count > 0 && !_busy;
    }

    private async void InstallFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_infPath is not null)
            await InstallAsync(_infPath, isFolder: false);
    }

    private async void InstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folderPath is not null)
            await InstallAsync(_folderPath, isFolder: true);
    }

    // ---------- Installation ----------

    private async Task InstallAsync(string path, bool isFolder)
    {
        SetBusy(true, isFolder ? "Installation des pilotes du dossier…" : "Installation du pilote…");
        ResultBar.IsOpen = false;
        OutputExpander.Visibility = Visibility.Collapsed;

        try
        {
            var restore = AppSettings.Current.CreateRestorePoint;
            var result = await DriverPackageService.AddAsync(path, isFolder, restore);

            if (result.Outcome == PackageOutcome.RestoreFailed)
            {
                var proceed = await AskAsync("Point de restauration impossible",
                    $"{result.Message}\n\nVoulez-vous installer le pilote sans point de restauration ?", "Installer sans", "Annuler");
                if (!proceed)
                {
                    ShowResult(InfoBarSeverity.Informational, "Installation annulée", "Aucun pilote n'a été installé.", "");
                    return;
                }
                result = await DriverPackageService.AddAsync(path, isFolder, restorePoint: false);
            }

            var name = isFolder ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileName(path);
            var success = result.Outcome is PackageOutcome.Installed or PackageOutcome.AddedNoDevice or PackageOutcome.RebootRequired;
            if (result.Outcome != PackageOutcome.Cancelled)
            {
                HistoryService.Add([new HistoryEntry
                {
                    Date = DateTime.Now,
                    DeviceName = name,
                    Category = isFolder ? "Installation depuis un dossier" : "Installation manuelle",
                    Title = path,
                    Success = success,
                    Message = result.Message,
                }]);
            }

            ShowResult(
                result.Outcome switch
                {
                    PackageOutcome.Installed => InfoBarSeverity.Success,
                    PackageOutcome.RebootRequired => InfoBarSeverity.Warning,
                    PackageOutcome.AddedNoDevice => InfoBarSeverity.Informational,
                    PackageOutcome.Cancelled => InfoBarSeverity.Informational,
                    _ => InfoBarSeverity.Error,
                },
                result.Outcome switch
                {
                    PackageOutcome.Installed => "Installation réussie",
                    PackageOutcome.RebootRequired => "Redémarrage nécessaire",
                    PackageOutcome.AddedNoDevice => "Pilote ajouté",
                    PackageOutcome.Cancelled => "Installation annulée",
                    _ => "Installation impossible",
                },
                result.Message,
                result.Output,
                result.Outcome == PackageOutcome.RebootRequired);
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "Installation impossible", ex.Message, "");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message, string output, bool reboot = false)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        RestartButton.Visibility = reboot ? Visibility.Visible : Visibility.Collapsed;
        ResultBar.IsOpen = true;

        OutputText.Text = output;
        OutputExpander.Visibility = string.IsNullOrWhiteSpace(output) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => AdminService.RestartComputer();

    // ---------- Détection ----------

    private async void DetectButton_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "Recherche des périphériques en erreur…");
        DevicesPanel.Children.Clear();

        try
        {
            var problems = await DeviceHealthService.GetProblemsAsync();
            if (problems.Count == 0)
            {
                DevicesSummary.Text = "Aucun périphérique en erreur : Windows reconnaît tout le matériel de ce PC.";
                return;
            }

            DevicesSummary.Text = problems.Count == 1 ? "1 périphérique a un problème." : $"{problems.Count} périphériques ont un problème.";
            BusyText.Text = "Recherche de pilotes sur Windows Update…";

            IReadOnlyList<DriverItem> updates = [];
            try
            {
                updates = DriverUpdateService.LastResult ?? await DriverUpdateService.SearchAsync();
            }
            catch (Exception)
            {
                // Sans réponse de Windows Update, on propose la recherche sur le web
            }

            foreach (var device in problems)
                DevicesPanel.Children.Add(BuildDeviceCard(device, updates));
        }
        catch (Exception ex)
        {
            DevicesSummary.Text = "La détection a échoué : " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Border BuildDeviceCard(ProblemDevice device, IReadOnlyList<DriverItem> updates)
    {
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var match = updates.FirstOrDefault(u => device.HardwareIds.Contains(u.HardwareId, StringComparer.OrdinalIgnoreCase));

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = device.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = $"{device.Explanation} (code {device.ErrorCode})", TextWrapping = TextWrapping.Wrap });
        if (device.Vendor.Length > 0)
            text.Children.Add(new TextBlock { Text = "Fabricant d'après son identifiant : " + device.Vendor, Style = caption, Foreground = secondary });
        text.Children.Add(new TextBlock { Text = device.MainId, Style = caption, Foreground = secondary, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Text = match is not null ? $"Pilote trouvé sur Windows Update : {match.Title}" : "Aucun pilote trouvé sur Windows Update.",
            Foreground = match is not null ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] : secondary,
            TextWrapping = TextWrapping.Wrap,
        });

        var button = new Button { Content = match is not null ? "Voir la mise à jour" : "Chercher sur le web", VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) =>
        {
            if (match is not null)
                App.MainWindow?.NavigateTo("Updates");
            else
                WebLinks.Search($"{device.MainId} driver");
        };

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = grid,
        };
    }

    // ---------- Utilitaires ----------

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        BrowseFileButton.IsEnabled = BrowseFolderButton.IsEnabled = DetectButton.IsEnabled = !busy;
        InstallFileButton.IsEnabled = !busy && _infPath is not null;
        InstallFolderButton.IsEnabled = !busy && _folderPath is not null;
    }

    private async Task<bool> AskAsync(string title, string message, string yes, string no)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            CloseButtonText = no,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
