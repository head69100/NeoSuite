using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeoDriver.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeoDriver.Views;

public sealed partial class ToolsPage : Page
{
    private string? _backupFolder;
    private string? _reportFile;
    private readonly List<(CheckBox Box, CleanupCandidate Candidate)> _rows = [];

    public ToolsPage()
    {
        InitializeComponent();
    }

    // ======================================================================
    // Sauvegarde
    // ======================================================================

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFolderAsync() is not { } parent)
            return;

        var folder = Path.Combine(parent.Path, $"Sauvegarde des pilotes {DateTime.Now:yyyy-MM-dd HH-mm}");
        BackupButton.IsEnabled = false;
        BackupBar.IsOpen = false;
        BackupProgress.Visibility = Visibility.Visible;
        try
        {
            var (success, message, count, size) = await DriverPackageService.BackupAsync(folder);
            _backupFolder = success ? folder : null;
            BackupBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            BackupBar.Title = success ? "Sauvegarde terminée" : "Sauvegarde impossible";
            BackupBar.Message = success ? $"{message} {FormatSize(size)} dans « {folder} »." : message;
            OpenBackupButton.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
            BackupBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            BackupBar.Severity = InfoBarSeverity.Error;
            BackupBar.Title = "Sauvegarde impossible";
            BackupBar.Message = ex.Message;
            BackupBar.IsOpen = true;
        }
        finally
        {
            BackupProgress.Visibility = Visibility.Collapsed;
            BackupButton.IsEnabled = true;
        }
    }

    private void OpenBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupFolder is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupFolder}\"") { UseShellExecute = true });
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("Install");

    // ======================================================================
    // Nettoyage
    // ======================================================================

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeButton.IsEnabled = false;
        CleanBar.IsOpen = false;
        CleanProgress.Visibility = Visibility.Visible;
        CleanList.Children.Clear();
        _rows.Clear();
        DeleteButton.Visibility = Visibility.Collapsed;
        CleanSummary.Visibility = Visibility.Collapsed;

        try
        {
            var candidates = await DriverStoreService.FindCleanupCandidatesAsync();
            ShowCandidates(candidates);
        }
        catch (Exception ex)
        {
            CleanBar.Severity = InfoBarSeverity.Error;
            CleanBar.Title = "Analyse impossible";
            CleanBar.Message = ex.Message;
            CleanBar.IsOpen = true;
        }
        finally
        {
            CleanProgress.Visibility = Visibility.Collapsed;
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void ShowCandidates(List<CleanupCandidate> candidates)
    {
        CleanSummary.Visibility = Visibility.Visible;
        if (candidates.Count == 0)
        {
            CleanSummary.Text = "Rien à nettoyer : chaque pilote n'existe qu'en une version, ou ses anciennes versions sont encore utilisées.";
            return;
        }

        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        foreach (var c in candidates)
        {
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock
            {
                Text = $"{c.Driver.Provider} · {c.Driver.Original} · version {c.Driver.VersionText}" + (c.Driver.Date is { } d ? $" du {d:dd/MM/yyyy}" : ""),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = $"Remplacé par la version {c.Kept.VersionText} · {c.Driver.ClassName} · {(c.Size > 0 ? FormatSize(c.Size) : "taille inconnue")}",
                Style = caption,
                Foreground = secondary,
            });
            var box = new CheckBox { IsChecked = true, Content = text, MinWidth = 0 };
            box.Checked += (_, _) => UpdateSelection();
            box.Unchecked += (_, _) => UpdateSelection();
            _rows.Add((box, c));
            CleanList.Children.Add(box);
        }
        DeleteButton.Visibility = Visibility.Visible;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).ToList();
        var size = chosen.Sum(r => r.Candidate.Size);
        CleanSummary.Text = $"{_rows.Count} ancien{(_rows.Count > 1 ? "s" : "")} pilote{(_rows.Count > 1 ? "s" : "")} trouvé{(_rows.Count > 1 ? "s" : "")}"
                            + (size > 0 ? $" · {FormatSize(size)} à libérer avec la sélection" : "");
        DeleteButton.Content = chosen.Count == 0 ? "Supprimer la sélection" : $"Supprimer {chosen.Count} pilote{(chosen.Count > 1 ? "s" : "")}";
        DeleteButton.IsEnabled = chosen.Count > 0;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Candidate).ToList();
        if (chosen.Count == 0)
            return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Supprimer {chosen.Count} ancien{(chosen.Count > 1 ? "s" : "")} pilote{(chosen.Count > 1 ? "s" : "")} ?",
            Content = new TextBlock
            {
                Text = "Les versions récentes sont conservées. Vous ne pourrez plus revenir à ces anciennes versions avec « Revenir à l'ancien pilote ».",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        DeleteButton.IsEnabled = false;
        AnalyzeButton.IsEnabled = false;
        CleanProgress.Visibility = Visibility.Visible;
        try
        {
            var (run, codes) = await DriverStoreService.DeleteAsync(chosen.Select(c => c.Driver.Published));
            if (run.Cancelled)
            {
                CleanBar.Severity = InfoBarSeverity.Informational;
                CleanBar.Title = "Nettoyage annulé";
                CleanBar.Message = "Sans la confirmation de Windows, NeoDriver ne peut pas supprimer de pilote.";
            }
            else
            {
                var done = chosen.Where(c => codes.TryGetValue(c.Driver.Published, out var code) && code is 0 or 3010).ToList();
                var failed = chosen.Count - done.Count;
                CleanBar.Severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                CleanBar.Title = failed == 0 ? "Nettoyage terminé" : "Nettoyage partiel";
                CleanBar.Message = $"{done.Count} pilote{(done.Count > 1 ? "s" : "")} supprimé{(done.Count > 1 ? "s" : "")}"
                                   + (done.Sum(c => c.Size) is > 0 and var freed ? $" · {FormatSize(freed)} libérés" : "")
                                   + (failed > 0 ? $". {failed} n'ont pas pu l'être : Windows les utilise encore." : ".");

                foreach (var (box, candidate) in _rows.ToList())
                {
                    if (done.Contains(candidate))
                    {
                        CleanList.Children.Remove(box);
                        _rows.RemoveAll(r => r.Box == box);
                    }
                }
                if (_rows.Count == 0)
                {
                    CleanSummary.Visibility = Visibility.Collapsed;
                    DeleteButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    UpdateSelection();
                }
            }
            CleanBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            CleanBar.Severity = InfoBarSeverity.Error;
            CleanBar.Title = "Nettoyage impossible";
            CleanBar.Message = ex.Message;
            CleanBar.IsOpen = true;
        }
        finally
        {
            CleanProgress.Visibility = Visibility.Collapsed;
            AnalyzeButton.IsEnabled = true;
            DeleteButton.IsEnabled = _rows.Count > 0;
        }
    }

    // ======================================================================
    // Rapport
    // ======================================================================

    private async void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"Rapport NeoDriver {Environment.MachineName} {DateTime.Now:yyyy-MM-dd}",
        };
        picker.FileTypeChoices.Add("Fichier texte", [".txt"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSaveFileAsync() is not { } file)
            return;

        await RunReportAsync(async report =>
        {
            await File.WriteAllTextAsync(file.Path, report, new System.Text.UTF8Encoding(true));
            _reportFile = file.Path;
            return ($"Rapport enregistré dans « {file.Path} ».", true);
        });
    }

    private async void CopyReportButton_Click(object sender, RoutedEventArgs e) =>
        await RunReportAsync(report =>
        {
            var package = new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
            return Task.FromResult(("Rapport copié : collez-le (Ctrl + V) dans votre message.", false));
        });

    private async Task RunReportAsync(Func<string, Task<(string Message, bool HasFile)>> output)
    {
        ReportButton.IsEnabled = CopyReportButton.IsEnabled = false;
        ReportBar.IsOpen = false;
        ReportProgress.Visibility = Visibility.Visible;
        try
        {
            var report = await ReportService.BuildAsync();
            var (message, hasFile) = await output(report);
            ReportBar.Severity = InfoBarSeverity.Success;
            ReportBar.Title = "Rapport prêt";
            ReportBar.Message = message;
            ShowReportButton.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ReportBar.Severity = InfoBarSeverity.Error;
            ReportBar.Title = "Rapport impossible";
            ReportBar.Message = ex.Message;
            ShowReportButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            ReportBar.IsOpen = true;
            ReportProgress.Visibility = Visibility.Collapsed;
            ReportButton.IsEnabled = CopyReportButton.IsEnabled = true;
        }
    }

    private void ShowReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reportFile is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_reportFile}\"") { UseShellExecute = true });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };
}
