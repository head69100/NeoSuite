using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

/// <summary>
/// Moniteur d'installation : cliché avant l'installation, cliché après, puis liste de ce que
/// l'installation a créé. Ces éléments sont rejoués automatiquement dans l'analyse des restes.
/// </summary>
public sealed partial class MonitorPage : Page
{
    public MonitorPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void Refresh()
    {
        var pending = InstallMonitor.HasPendingSnapshot;
        StateRing.IsActive = pending;
        CancelButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        SnapshotLabel.Text = pending ? "Installation terminée" : "Photographier avant";
        StateTitle.Text = pending ? "Cliché pris : installez le logiciel maintenant" : "Aucun cliché en attente";
        StateText.Text = pending
            ? "Lancez l'installateur du logiciel (ou laissez-le faire), puis revenez cliquer sur « Installation terminée »."
            : "Photographiez le PC avant et après l'installation : la différence donnera une désinstallation parfaite.";

        var records = InstallMonitor.LoadAll();
        RecordsPanel.Children.Clear();
        EmptyPanel.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsTitle.Visibility = records.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (file, record) in records)
            RecordsPanel.Children.Add(BuildCard(file, record));
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        SnapshotButton.IsEnabled = false;
        try
        {
            if (!InstallMonitor.HasPendingSnapshot)
            {
                SetResult(InfoBarSeverity.Informational, "Cliché en cours", "NeoUninstaller photographie les dossiers, le démarrage et le registre…");
                await InstallMonitor.StartAsync();
                SetResult(InfoBarSeverity.Success, "Cliché avant pris", "Vous pouvez maintenant lancer l'installateur du logiciel.");
            }
            else
            {
                var name = await AskNameAsync();
                if (name is null)
                    return;
                var record = await InstallMonitor.FinishAsync(name);
                SetResult(InfoBarSeverity.Success, $"{record.Items.Count} éléments créés pendant l'installation",
                    record.ProgramNames.Count > 0
                        ? $"Programmes détectés : {string.Join(", ", record.ProgramNames)}. Ils seront proposés à la désinstallation."
                        : "Ces éléments seront proposés automatiquement quand vous désinstallerez ce logiciel.");
            }
        }
        catch (Exception ex)
        {
            SetResult(InfoBarSeverity.Error, "Le moniteur a échoué", ex.Message);
        }
        SnapshotButton.IsEnabled = true;
        Refresh();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        InstallMonitor.Cancel();
        SetResult(InfoBarSeverity.Informational, "Cliché annulé", "Aucun élément n'a été enregistré.");
        Refresh();
    }

    /// <summary>Nom du logiciel installé (facultatif : les programmes détectés servent de repli).</summary>
    private async Task<string?> AskNameAsync()
    {
        var box = new TextBox { PlaceholderText = "Ex. : MonLogiciel", Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Quel logiciel venez-vous d'installer ?",
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Ce nom sert à retrouver les restes à la désinstallation. Laissez vide pour utiliser les programmes que Windows vient de déclarer.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "Terminer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return string.IsNullOrWhiteSpace(box.Text) ? "" : box.Text.Trim();
    }

    private FrameworkElement BuildCard(string file, MonitorRecord record)
    {
        var details = string.Join(" · ", new[]
        {
            $"{record.Date:dd/MM/yyyy HH:mm}",
            $"{record.Items.Count} éléments",
            record.ProgramNames.Count > 0 ? string.Join(", ", record.ProgramNames) : null,
        }.Where(s => s is not null));

        var view = new Button { Content = "Voir les éléments" };
        view.Click += async (_, _) => await ShowItemsAsync(record);
        var open = new Button { Content = "Ouvrir" };
        open.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{file}\"");
        var remove = new Button { Content = "Supprimer" };
        remove.Click += async (_, _) => await RemoveAsync(file, record);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(view);
        buttons.Children.Add(open);
        buttons.Children.Add(remove);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = record.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = details, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16, 12, 16, 12), Child = grid };
    }

    private async Task ShowItemsAsync(MonitorRecord record)
    {
        var lines = record.Items
            .Select(i => i.Kind switch
            {
                LeftoverKind.RegistryValue => $"{i.Path}  →  {i.ValueName}",
                LeftoverKind.Service or LeftoverKind.ScheduledTask => $"{i.ValueName}",
                _ => i.Path,
            })
            .ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{record.Name} — {record.Items.Count} éléments créés",
            Content = new ScrollViewer
            {
                MaxHeight = 400,
                Content = new TextBlock
                {
                    Text = lines.Count > 0 ? string.Join("\n", lines) : "Rien n'a été créé (installation déjà présente avant le cliché ?).",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "Fermer",
        };
        await dialog.ShowAsync();
    }

    private async Task RemoveAsync(string file, MonitorRecord record)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Supprimer « {record.Name} » ?",
            Content = new TextBlock
            {
                Text = "L'enregistrement du moniteur sera supprimé. Rien d'autre n'est touché : cette installation ne sera plus proposée dans l'analyse des restes.",
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
            File.Delete(file);
        }
        catch (Exception ex)
        {
            SetResult(InfoBarSeverity.Error, "Suppression impossible", ex.Message);
        }
        Refresh();
    }

    private void SetResult(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
