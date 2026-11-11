using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load();

    private void Load()
    {
        ReportsPanel.Children.Clear();
        var reports = LeftoverCleaner.LoadReports();
        EmptyPanel.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var report in reports)
        {
            var hasRegistry = Directory.Exists(report.BackupFolder) && Directory.EnumerateFiles(report.BackupFolder, "*.reg").Any();
            var fileCount = report.Items.Count(i => i.Success && i.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut);

            var restore = new Button { Content = "Tout annuler", IsEnabled = hasRegistry || fileCount > 0 };
            restore.Click += async (_, _) => await RestoreAsync(report, hasRegistry, fileCount);
            var open = new Button { Content = "Ouvrir la sauvegarde" };
            open.Click += (_, _) => Process.Start("explorer.exe", $"\"{report.BackupFolder}\"");

            var details = report.Failed == 0
                ? $"{report.Succeeded} éléments supprimés"
                : $"{report.Succeeded} éléments supprimés, {report.Failed} en échec";

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon { Glyph = report.Failed == 0 ? "" : "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = report.ProgramName, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            text.Children.Add(new TextBlock { Text = $"{report.Date:dd/MM/yyyy HH:mm} · {details}", Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(open);
            buttons.Children.Add(restore);

            Grid.SetColumn(text, 1);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(icon);
            grid.Children.Add(text);
            grid.Children.Add(buttons);

            ReportsPanel.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16, 12, 16, 12), Child = grid });
        }
    }

    private async Task RestoreAsync(CleanReport report, bool hasRegistry, int fileCount)
    {
        var parts = new List<string>();
        if (hasRegistry)
            parts.Add("les clés de registre supprimées seront réimportées");
        if (fileCount > 0)
            parts.Add($"les {fileCount} fichier(s) et dossier(s) seront remis à leur place depuis la Corbeille (s'ils y sont encore, 7 jours maximum)");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Tout annuler pour {report.ProgramName} ?",
            Content = new TextBlock
            {
                Text = string.Join(", et ", parts) + ".",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Tout restaurer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        int restoredFiles = 0, missing = 0;
        if (fileCount > 0)
        {
            var paths = report.Items
                .Where(i => i.Success && i.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut)
                .Select(i => i.Path);
            (restoredFiles, missing) = await RecycleBinRestore.RestoreAsync(paths);
        }

        var (restored, failed) = hasRegistry ? await LeftoverCleaner.RestoreRegistryAsync(report.BackupFolder) : (0, 0);
        var ok = failed == 0 && missing == 0;
        ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title = ok ? "Suppression annulée" : "Restauration partielle";
        var messages = new List<string>();
        if (restored > 0)
            messages.Add($"{restored} sauvegarde(s) de registre réimportée(s)");
        if (restoredFiles > 0)
            messages.Add($"{restoredFiles} élément(s) remis à leur place");
        if (failed > 0)
            messages.Add($"{failed} clé(s) en échec (droits administrateur requis)");
        if (missing > 0)
            messages.Add($"{missing} élément(s) introuvable(s) dans la Corbeille (vidée ou purgée)");
        ResultBar.Message = string.Join(" · ", messages);
        ResultBar.IsOpen = true;
        Load();
    }
}
