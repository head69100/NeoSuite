using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

/// <summary>
/// Assistant façon Revo Uninstaller : point de restauration → désinstalleur officiel →
/// analyse des restes → suppression réversible des éléments cochés.
/// </summary>
public sealed partial class UninstallPage : Page
{
    private enum Step { Prepare, Uninstall, Scan, Clean }
    private enum StepState { Pending, Active, Done, Skipped, Failed }

    private static readonly string[] StepNames = ["Préparation", "Désinstallation", "Analyse des restes", "Nettoyage"];

    private InstalledProgram _program = null!;
    private List<Leftover> _leftovers = [];
    private readonly Border[] _stepPills = new Border[4];
    private TaskCompletionSource<string>? _choice;
    private CancellationTokenSource? _stopWaiting;
    private string? _backupFolder;
    private readonly List<string> _notes = [];

    public UninstallPage()
    {
        InitializeComponent();
        for (var i = 0; i < 4; i++)
        {
            _stepPills[i] = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1) };
            Grid.SetColumn(_stepPills[i], i);
            StepsGrid.Children.Add(_stepPills[i]);
            SetStep((Step)i, StepState.Pending);
        }
    }

    private List<InstalledProgram> _queue = [];
    private bool _aborted;
    private bool _restorePointDone;
    private bool _deferScan;   // lot : on désinstalle tout, l'analyse des restes vient à la fin
    private bool _final;      // dernière phase du lot : analyse groupée
    private readonly List<InstalledProgram> _uninstalled = [];

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _queue = e.Parameter switch
        {
            InstalledProgram single => [single],
            IEnumerable<InstalledProgram> many => many.ToList(),
            _ => [],
        };

        // Lot : on désinstalle d'abord tous les programmes, puis une seule analyse des restes pour l'ensemble
        _deferScan = _queue.Count > 1;
        await AskRelatedAsync();
        for (var i = 0; i < _queue.Count && !_aborted; i++)
        {
            ShowProgram(_queue[i], i);
            await RunOneAsync();
        }
        if (!_aborted && _deferScan && _uninstalled.Count > 0)
            await ScanAndCleanBatchAsync();
        if (!_aborted)
            GoBack();
    }

    /// <summary>
    /// Programmes qui vont avec ceux qu'on désinstalle (Chrome et Google Update, Office et ses modules,
    /// composants NVIDIA/Adobe…) : proposés avant de commencer, jamais ajoutés sans accord.
    /// </summary>
    private async Task AskRelatedAsync()
    {
        var related = RelatedPrograms.Find(_queue, AppState.Programs);
        if (related.Count == 0)
            return;

        var boxes = new List<(CheckBox Box, InstalledProgram Program)>();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Ces programmes vont avec votre choix et risquent de rester orphelins. Cochez ceux à désinstaller avec :",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var r in related)
        {
            var box = new CheckBox
            {
                Content = $"{r.Program.DisplayName} — {r.Reason}",
                IsChecked = r.Suggested,
            };
            boxes.Add((box, r.Program));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Programmes liés",
            Content = new ScrollViewer { MaxHeight = 320, Content = panel },
            PrimaryButtonText = "Continuer",
            CloseButtonText = "Tout désinstaller seul",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return; // l'utilisateur préfère ne toucher qu'à sa sélection

        foreach (var (box, program) in boxes)
            if (box.IsChecked == true && !_queue.Any(p => p.Key == program.Key))
                _queue.Add(program);
    }

    private void ShowProgram(InstalledProgram program, int index)
    {
        _program = program;
        _leftovers = [];
        _notes.Clear();
        NoteBar.IsOpen = false;
        ResultsPanel.Visibility = Visibility.Collapsed;
        LeftoverList.ItemsSource = null;
        for (var s = 0; s < 4; s++)
            SetStep((Step)s, StepState.Pending);

        var batch = _queue.Count > 1 ? $" ({index + 1} sur {_queue.Count})" : "";
        ProgramName.Text = (program.IsForced ? $"Nettoyage de {program.DisplayName}" : $"Désinstallation de {program.DisplayName}") + batch;
        ProgramDetails.Text = string.Join(" · ", new[] { program.Publisher, program.Version, LeftoverScanner.ResolveInstallDir(program) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        ProgramIcon.Source = program.Icon;
        FallbackIcon.Visibility = program.Icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunOneAsync()
    {
        BackButton.IsEnabled = false;
        try
        {
            await PrepareAsync();
            if (!await UninstallAsync())
                return;
            if (_deferScan)
                _uninstalled.Add(_program);
            else
                await ScanAndCleanAsync();
        }
        finally
        {
            BackButton.IsEnabled = true;
            ShowNotes();
        }
    }

    // ---------- 1. Préparation ----------

    private async Task PrepareAsync()
    {
        SetStep(Step.Prepare, StepState.Active);
        // En lot, un seul point de restauration avant le premier programme suffit
        if (!AppSettings.Current.CreateRestorePoint || _restorePointDone)
        {
            SetStep(Step.Prepare, StepState.Skipped);
            return;
        }
        if (!AdminService.IsAdministrator)
        {
            _notes.Add("Aucun point de restauration n'a été créé : il faut lancer NeoUninstaller en administrateur.");
            SetStep(Step.Prepare, StepState.Skipped);
            return;
        }

        SetStatus("Création d'un point de restauration…", "Pour pouvoir revenir en arrière en cas de problème.");
        try
        {
            var description = _queue.Count > 1 ? $"NeoUninstaller - {_queue.Count} programmes" : $"NeoUninstaller - {_program.DisplayName}";
            if (!await RestorePointService.CreateAsync(description))
                _notes.Add("Windows n'a pas créé de nouveau point de restauration : il en existe déjà un de moins de 24 h.");
            _restorePointDone = true;
            SetStep(Step.Prepare, StepState.Done);
        }
        catch (Exception ex)
        {
            _notes.Add($"Point de restauration impossible : {ex.Message}");
            SetStep(Step.Prepare, StepState.Failed);
        }
    }

    // ---------- 2. Désinstalleur officiel ----------

    /// <summary>Renvoie false si l'utilisateur arrête là.</summary>
    private async Task<bool> UninstallAsync()
    {
        if (_program.IsForced)
        {
            SetStep(Step.Uninstall, StepState.Skipped);
            return true;
        }

        SetStep(Step.Uninstall, StepState.Active);
        SetStatus($"Le désinstalleur de {_program.DisplayName} est ouvert",
            "Suivez ses étapes. NeoUninstaller attend qu'il se termine, puis cherchera les restes.",
            secondary: "Il est terminé");
        _stopWaiting = new CancellationTokenSource();

        try
        {
            await UninstallRunner.RunAsync(_program, _stopWaiting.Token);
        }
        catch (Exception ex)
        {
            _stopWaiting = null;
            SetStep(Step.Uninstall, StepState.Failed);
            SetStatus(ex is OperationCanceledException ? "Désinstallation annulée" : "Le désinstalleur n'a pas pu être lancé", ex.Message,
                icon: "", primary: _deferScan ? "Continuer" : "Chercher les restes quand même", secondary: StopLabel);
            return await WaitChoiceAsync() == "primary";
        }

        _stopWaiting = null;
        if (ProgramInventory.StillInstalled(_program))
        {
            SetStep(Step.Uninstall, StepState.Failed);
            SetStatus($"{_program.DisplayName} semble toujours installé",
                "La désinstallation a peut-être été annulée. Vous pouvez quand même chercher des restes.",
                icon: "", primary: _deferScan ? "Continuer" : "Chercher les restes", secondary: StopLabel);
            return await WaitChoiceAsync() == "primary";
        }

        SetStep(Step.Uninstall, StepState.Done);
        if (!AppSettings.Current.AutoScan && !_deferScan)
        {
            SetStatus($"{_program.DisplayName} a été désinstallé", "Cherchez maintenant les fichiers et clés de registre qu'il a laissés.",
                icon: "", primary: "Chercher les restes", secondary: FinishLabel);
            return await WaitChoiceAsync() == "primary";
        }
        return true;
    }

    // ---------- 3 et 4. Analyse puis nettoyage ----------

    /// <summary>Fin d'un lot : un seul écran de restes pour tous les programmes désinstallés.</summary>
    private async Task ScanAndCleanBatchAsync()
    {
        _final = true;
        _leftovers = [];
        _notes.Clear();
        NoteBar.IsOpen = false;
        ResultsPanel.Visibility = Visibility.Collapsed;
        LeftoverList.ItemsSource = null;
        SetStep(Step.Prepare, StepState.Done);
        SetStep(Step.Uninstall, StepState.Done);
        SetStep(Step.Scan, StepState.Pending);
        SetStep(Step.Clean, StepState.Pending);
        ProgramName.Text = $"Restes de {_uninstalled.Count} programmes désinstallés";
        ProgramDetails.Text = string.Join(" · ", _uninstalled.Select(p => p.DisplayName));
        FallbackIcon.Visibility = Visibility.Visible;
        ProgramIcon.Source = null;
        BackButton.IsEnabled = false;

        SetStep(Step.Scan, StepState.Active);
        SetStatus("Recherche des restes…", $"Analyse de {_uninstalled.Count} programmes : dossiers, raccourcis, registre, démarrage automatique, services.");
        var keys = _uninstalled.Select(p => p.Key).ToHashSet();
        var others = AppState.Programs.Where(p => !keys.Contains(p.Key)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in _uninstalled)
        {
            var found = await LeftoverScanner.ScanAsync(program, others, AdminService.IsAdministrator);
            found.AddRange(InstallMonitor.ForProgram(program, AdminService.IsAdministrator));
            foreach (var l in found)
                if (seen.Add($"{l.Kind}|{l.Path}|{l.ValueName}"))
                    _leftovers.Add(l);
        }
        _program = _uninstalled[0];
        await ReviewAndCleanAsync($"{_uninstalled.Count} programmes");
    }

    private async Task ScanAndCleanAsync()
    {
        SetStep(Step.Scan, StepState.Active);
        SetStatus("Recherche des restes…", "Dossiers, raccourcis, registre, démarrage automatique, services.");
        _leftovers = await LeftoverScanner.ScanAsync(_program, AppState.Programs, AdminService.IsAdministrator);
        var seen = new HashSet<string>(_leftovers.Select(l => $"{l.Kind}|{l.Path}|{l.ValueName}"), StringComparer.OrdinalIgnoreCase);
        foreach (var l in InstallMonitor.ForProgram(_program, AdminService.IsAdministrator))
            if (seen.Add($"{l.Kind}|{l.Path}|{l.ValueName}"))
                _leftovers.Add(l);
        await ReviewAndCleanAsync(_program.DisplayName);
    }

    private async Task ReviewAndCleanAsync(string label)
    {
        SetStep(Step.Scan, StepState.Done);

        if (_leftovers.Count == 0)
        {
            SetStep(Step.Clean, StepState.Skipped);
            SetStatus("Aucun reste trouvé", _final ? "Les programmes désinstallés n'ont rien laissé derrière eux." : $"{_program.DisplayName} n'a rien laissé derrière lui.", icon: "", primary: FinishLabel);
            await WaitChoiceAsync();
            return;
        }

        LeftoverList.ItemsSource = _leftovers;
        ResultsPanel.Visibility = Visibility.Visible;
        UpdateSummary();
        foreach (var item in _leftovers)
            item.PropertyChanged += (_, _) => UpdateSummary();

        SetStep(Step.Clean, StepState.Active);
        SetStatus($"{_leftovers.Count} restes trouvés",
            "Les éléments « Sûr » sont cochés. Vérifiez les éléments « À vérifier » avant de les cocher. " +
            "Les fichiers iront dans la Corbeille et le registre sera sauvegardé.",
            icon: "", primary: "Supprimer la sélection", secondary: "Ignorer");

        while (true)
        {
            if (await WaitChoiceAsync() != "primary")
            {
                SetStep(Step.Clean, StepState.Skipped);
                return;
            }
            if (_leftovers.Any(l => l.IsChecked))
                break;
            SetStatus("Aucun élément coché", "Cochez au moins un élément, ou cliquez sur « Ignorer ».",
                icon: "", primary: "Supprimer la sélection", secondary: "Ignorer");
        }

        var selected = _leftovers.Where(l => l.IsChecked).ToList();
        SetStatus("Nettoyage…", $"Suppression de {selected.Count} éléments.");
        ResultsPanel.Visibility = Visibility.Collapsed;
        var report = await LeftoverCleaner.CleanAsync(label, selected);
        _backupFolder = report.BackupFolder;

        foreach (var failed in report.Items.Where(i => !i.Success))
            _notes.Add($"Non supprimé : {failed.Path}{(failed.ValueName is null ? "" : $" → {failed.ValueName}")} ({failed.Error})");

        SetStep(Step.Clean, report.Failed == 0 ? StepState.Done : StepState.Failed);
        SetStatus(report.Failed == 0 ? $"{label} : restes entièrement supprimés" : $"{report.Succeeded} éléments supprimés, {report.Failed} en échec",
            "Les fichiers sont dans la Corbeille et les clés de registre ont été sauvegardées (page Historique pour les restaurer).",
            icon: report.Failed == 0 ? "" : "", primary: FinishLabel, secondary: "Ouvrir la sauvegarde");

        while (await WaitChoiceAsync() == "secondary")
            Process.Start("explorer.exe", $"\"{_backupFolder}\"");
    }

    private string StopLabel => !_final && _queue.IndexOf(_program) < _queue.Count - 1 ? "Passer au suivant" : "Arrêter";

    /// <summary>En lot : « Programme suivant » tant qu'il en reste.</summary>
    private string FinishLabel => !_final && _queue.IndexOf(_program) < _queue.Count - 1 ? "Programme suivant" : "Terminer";

    private void UpdateSummary()
    {
        var checkedItems = _leftovers.Where(l => l.IsChecked).ToList();
        var size = checkedItems.Sum(l => l.SizeBytes);
        ResultsSummary.Text = $"{checkedItems.Count} sur {_leftovers.Count} éléments sélectionnés" + (size > 0 ? $" · {InstalledProgram.FormatSize(size)}" : "");
    }

    private void CheckCertain_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _leftovers)
            l.IsChecked = l.CanDelete && l.Confidence == Confidence.Certain;
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _leftovers.Where(l => l.CanDelete))
            l.IsChecked = true;
    }

    private void CheckNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _leftovers)
            l.IsChecked = false;
    }

    // ---------- Affichage ----------

    private void SetStatus(string title, string text, string? icon = null, string? primary = null, string? secondary = null)
    {
        StatusTitle.Text = title;
        StatusText.Text = text;
        StatusRing.IsActive = icon is null;
        StatusRing.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        if (icon is not null)
        {
            StatusIcon.Glyph = icon;
            StatusIcon.Foreground = (Brush)Application.Current.Resources[icon switch
            {
                "" => "SystemFillColorSuccessBrush",
                "" or "" => "SystemFillColorCautionBrush",
                _ => "AccentTextFillColorPrimaryBrush",
            }];
        }
        PrimaryAction.Content = primary;
        PrimaryAction.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryAction.Content = secondary;
        SecondaryAction.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStep(Step step, StepState state)
    {
        var pill = _stepPills[(int)step];
        var (glyph, brushKey) = state switch
        {
            StepState.Active => ("", "AccentTextFillColorPrimaryBrush"),
            StepState.Done => ("", "SystemFillColorSuccessBrush"),
            StepState.Skipped => ("", "TextFillColorTertiaryBrush"),
            StepState.Failed => ("", "SystemFillColorCautionBrush"),
            _ => ("", "TextFillColorTertiaryBrush"),
        };
        var brush = (Brush)Application.Current.Resources[brushKey];
        pill.BorderBrush = state == StepState.Active ? brush : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        pill.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        pill.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 14, Foreground = brush },
                new TextBlock
                {
                    Text = $"{(int)step + 1}. {StepNames[(int)step]}",
                    FontWeight = state == StepState.Active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = state == StepState.Pending ? (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }

    private void ShowNotes()
    {
        if (_notes.Count == 0)
            return;
        NoteBar.Severity = InfoBarSeverity.Warning;
        NoteBar.Title = "À savoir";
        NoteBar.Message = string.Join("\n", _notes.Distinct());
        NoteBar.IsOpen = true;
    }

    private Task<string> WaitChoiceAsync()
    {
        ShowNotes();
        BackButton.IsEnabled = true;
        _choice = new TaskCompletionSource<string>();
        return _choice.Task;
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e) => _choice?.TrySetResult("primary");

    private void SecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        // Pendant le désinstalleur : « Il est terminé » arrête l'attente
        if (_stopWaiting is { IsCancellationRequested: false })
        {
            _stopWaiting.Cancel();
            return;
        }
        _choice?.TrySetResult("secondary");
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _aborted = true; // en lot : les programmes restants ne sont pas traités
        _choice?.TrySetResult("back");
        GoBack();
    }

    private void GoBack()
    {
        AppState.ListIsStale = true; // la liste sera relue en revenant sur la page Programmes
        App.MainWindow?.NavigateTo("Programs");
    }
}
