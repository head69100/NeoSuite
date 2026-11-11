using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NeoRar.Models;
using NeoRar.Services;
using SevenZip;
using Windows.Graphics;

namespace NeoRar;

/// <summary>
/// Petite fenêtre d'opération, comme celles de WinRAR : création d'une archive (formulaire puis progression)
/// ou extraction (mot de passe et conflits si besoin, puis progression).
/// </summary>
public sealed partial class TaskWindow : Window
{
    /// <summary>Niveaux partagés avec l'éditeur de sauvegardes de MainWindow (index → CompressionLevel).</summary>
    internal static readonly CompressionLevel[] Levels =
        [CompressionLevel.None, CompressionLevel.Fast, CompressionLevel.Low, CompressionLevel.Normal, CompressionLevel.High, CompressionLevel.Ultra];

    private CancellationTokenSource? _cancel;
    private Task? _running;
    private string? _resultPath;
    private DateTime _runStarted;

    // Opération en cours
    private string _resultTitle = "";
    private string _resultMessage = "";
    private bool _backgrounded;    // continuée après réduction de la fenêtre : à restaurer en fin d'opération
    private bool _closed;          // fenêtre déjà fermée : rien à restaurer
    private bool _closeOnResult;   // sauvegarde planifiée : fermeture après affichage (et notification) du résultat
    private BackupJob? _backup;    // sauvegarde en cours, le cas échéant
    private string? _historyAction; // rubrique d'historique de l'opération en cours
    private string? _historyTarget;

    // Création
    private IReadOnlyList<string> _sources = [];
    private Action<string>? _onCreated;

    // Extraction
    private string _archive = "";
    private string _destination = "";
    private string? _password;
    private IReadOnlyCollection<int>? _indexes;
    private bool _autoClose;
    private bool _checkConflicts;
    private Action? _onDone;

    private TaskWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        RootGrid.RequestedTheme = AppSettings.Current.Theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.Closing += AppWindow_Closing;
    }

    private int _width;
    private int _minHeight;
    private bool _centered;

    /// <summary>
    /// Ajuste la fenêtre à son contenu : largeur donnée, hauteur mesurée (au moins <paramref name="minHeight"/>).
    /// Une hauteur fixe coupait les boutons selon la longueur du nom d'archive ou le zoom de Windows.
    /// </summary>
    private void SetSize(int width, int minHeight)
    {
        _width = width;
        _minHeight = minHeight;
        if (RootGrid.IsLoaded)
            FitToContent();
        else
            RootGrid.Loaded += (_, _) => FitToContent();
    }

    private void FitToContent()
    {
        RootGrid.Measure(new Windows.Foundation.Size(_width, double.PositiveInfinity));
        var height = Math.Max(_minHeight, Math.Ceiling(RootGrid.DesiredSize.Height) + 4);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(_width * scale), (int)(height * scale));
        AppWindow.ResizeClient(size);

        // Centrée à la première apparition, puis laissée où l'utilisateur l'a mise
        if (_centered)
            return;
        _centered = true;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - outer.Width) / 2, area.Y + (area.Height - outer.Height) / 2));
    }

    // ---------- Création ----------

    /// <summary>Formulaire de création d'une archive à partir de fichiers et dossiers.</summary>
    public static void ShowCreate(IReadOnlyList<string> sources, Action<string>? onCreated = null)
    {
        var window = new TaskWindow { _sources = sources, _onCreated = onCreated };
        window.TitleText.Text = "Créer une archive";
        window.SubtitleText.Text = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\')) : $"{sources.Count} éléments";

        var settings = AppSettings.Current;
        window.FormatCombo.SelectedIndex = (int)settings.DefaultKind;
        window.LevelCombo.SelectedIndex = Math.Clamp(settings.DefaultLevel, 0, Levels.Length - 1);
        window.VolumeCombo.SelectedIndex = 0;
        var folder = Path.GetDirectoryName(sources[0].TrimEnd('\\')) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        window.ArchivePathBox.Text = ArchiveService.UniquePath(Path.Combine(folder, ArchiveService.SuggestArchiveName(sources, settings.DefaultKind)), false);
        window._historyAction = "Création";
        window._historyTarget = window.ArchivePathBox.Text;

        window.Show(window.FormPanel, 560, 470);
        window.Activate();
    }

    private ArchiveKind SelectedKind => (ArchiveKind)Math.Max(0, FormatCombo.SelectedIndex);

    /// <summary>Tailles de volumes proposées, en octets (0 = archive en un seul fichier).</summary>
    private static readonly long[] VolumeSizes = [0, 100L << 20, 700L << 20, 4700L << 20, 25_000L << 20];

    /// <summary>Taille des volumes choisie en octets (0 = aucun). Le format 7z seul sait découper.</summary>
    private long SelectedVolumeSize => SelectedKind == ArchiveKind.SevenZip && VolumeCombo.IsEnabled
        ? VolumeSizes[Math.Clamp(VolumeCombo.SelectedIndex, 0, VolumeSizes.Length - 1)]
        : 0;

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArchivePathBox is null)
            return;
        // Extension du nom de fichier assortie au format choisi
        var path = ArchivePathBox.Text.Trim();
        if (path.Length > 0)
            ArchivePathBox.Text = Path.ChangeExtension(path, ArchiveService.Extension(SelectedKind));

        var tar = SelectedKind == ArchiveKind.Tar;
        LevelCombo.IsEnabled = !tar;
        PasswordSection.Visibility = tar ? Visibility.Collapsed : Visibility.Visible;
        EncryptNamesCheck.Visibility = SelectedKind == ArchiveKind.SevenZip ? Visibility.Visible : Visibility.Collapsed;

        // Les volumes n'existent qu'au format 7z : le moteur refuse la découpe pour ZIP et TAR
        var sevenZip = SelectedKind == ArchiveKind.SevenZip;
        VolumeCombo.IsEnabled = sevenZip;
        VolumeHint.Text = sevenZip
            ? "Découpe l'archive en tomes .001, .002… — utile pour graver ou transporter de grosses archives (7z uniquement)."
            : "La découpe en volumes n'existe qu'au format 7z.";
        if (!sevenZip)
            VolumeCombo.SelectedIndex = 0;
    }

    private void ProtectCheck_Changed(object sender, RoutedEventArgs e) =>
        PasswordFields.Visibility = ProtectCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickSaveArchiveAsync(this, Path.GetFileName(ArchivePathBox.Text), SelectedKind) is { } path)
            ArchivePathBox.Text = path;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var path = ArchivePathBox.Text.Trim();
        string? error = null;
        if (path.Length == 0 || !Path.IsPathRooted(path))
            error = "Indiquez l'emplacement complet de l'archive.";
        else if (!Directory.Exists(Path.GetDirectoryName(path)))
            error = "Le dossier de destination n'existe pas.";
        else if (ProtectCheck.IsChecked == true && SelectedKind != ArchiveKind.Tar && PasswordBox1.Password.Length == 0)
            error = "Saisissez un mot de passe, ou décochez la protection.";
        else if (ProtectCheck.IsChecked == true && PasswordBox1.Password != PasswordBox2.Password)
            error = "Les deux mots de passe ne correspondent pas.";
        else if (_sources.Any(s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase)))
            error = "L'archive ne peut pas se contenir elle-même.";

        FormError.Text = error ?? "";
        FormError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is not null)
            return;

        if (File.Exists(path) || File.Exists(path + ".001"))
        {
            var replace = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Cette archive existe déjà",
                Content = $"Remplacer « {Path.GetFileName(path)} » ?",
                PrimaryButtonText = "Remplacer",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await replace.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        var settings = AppSettings.Current;
        settings.DefaultKind = SelectedKind;
        settings.DefaultLevel = LevelCombo.SelectedIndex;
        settings.Save();

        var options = new CompressOptions(SelectedKind, Levels[Math.Max(0, LevelCombo.SelectedIndex)],
            ProtectCheck.IsChecked == true && SelectedKind != ArchiveKind.Tar ? PasswordBox1.Password : null,
            EncryptNamesCheck.IsChecked == true, SelectedVolumeSize);

        TitleText.Text = "Compression";
        SubtitleText.Text = Path.GetFileName(path);
        _historyAction = "Création";
        _historyTarget = path;
        await RunAsync("Compression en cours…", (progress, token) => ArchiveService.CompressAsync(_sources, path, options, progress, token), () =>
        {
            _resultPath = path;
            // En volumes : le résultat compte les tomes .001/.002… et leur taille totale
            var parts = ArchiveService.PartFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            if (parts.Count > 0)
            {
                _resultPath = parts[0];
                var total = parts.Sum(p => new FileInfo(p).Length);
                ShowResult(InfoBarSeverity.Success, "Archive créée",
                    $"{Path.GetFileName(path)} · {parts.Count} tomes de {ArchiveEntry.FormatSize(options.VolumeSize)} · {ArchiveEntry.FormatSize(total)} au total");
                HistoryService.Add("Création", path, $"{parts.Count} tomes", true);
            }
            else
            {
                var size = new FileInfo(path).Length;
                ShowResult(InfoBarSeverity.Success, "Archive créée", $"{Path.GetFileName(path)} · {ArchiveEntry.FormatSize(size)}");
                HistoryService.Add("Création", path, ArchiveEntry.FormatSize(size), true);
            }
            _onCreated?.Invoke(path);
        });
    }

    /// <summary>Ajoute des fichiers dans un dossier d'une archive 7z ou ZIP existante (progression seule).</summary>
    public static void ShowAdd(ArchiveInfo archive, IReadOnlyList<string> sources, string folder, string? password, Action onDone)
    {
        var window = new TaskWindow
        {
            _onDone = onDone,
            _historyAction = "Ajout",
            _historyTarget = archive.FilePath,
        };
        window.TitleText.Text = "Ajout à l'archive";
        window.SubtitleText.Text = Path.GetFileName(archive.FilePath) + (folder.Length > 0 ? $" › {folder.Replace("/", " › ")}" : "");
        window.Activate();
        _ = window.RunAsync("Compression en cours…", (progress, token) => ArchiveService.AddAsync(archive, sources, folder, password, progress, token), () =>
        {
            HistoryService.Add("Ajout", archive.FilePath, $"{sources.Count} élément(s)", true);
            window._onDone?.Invoke();
            // La fenêtre se referme : la bulle indique que l'ajout est bien parti
            Notify.Show("NeoRar", $"Ajout terminé — {Path.GetFileName(archive.FilePath)}");
            window.Close();
        });
    }

    // ---------- Extraction ----------

    /// <summary>
    /// Extrait <paramref name="archive"/> dans <paramref name="destination"/> (tout, ou les éléments <paramref name="indexes"/>).
    /// <paramref name="autoClose"/> : fermeture automatique en cas de succès (extraction depuis l'Explorateur).
    /// </summary>
    public static void ShowExtract(string archive, string destination, string? password = null, IReadOnlyCollection<int>? indexes = null,
        bool autoClose = false, bool checkConflicts = false, Action? onDone = null)
    {
        var window = new TaskWindow
        {
            _archive = archive,
            _destination = destination,
            _password = password,
            _indexes = indexes,
            _autoClose = autoClose,
            _checkConflicts = checkConflicts,
            _onDone = onDone,
            _historyAction = "Extraction",
            _historyTarget = destination,
        };
        window.TitleText.Text = "Extraction";
        window.SubtitleText.Text = Path.GetFileName(archive);
        window.Activate();
        _ = window.PrepareExtractAsync();
    }

    private async Task PrepareExtractAsync()
    {
        if (_checkConflicts)
        {
            // « Extraire ici » : prévenir avant d'écraser des fichiers déjà présents
            try
            {
                var info = await ArchiveService.OpenAsync(_archive, _password);
                var existing = info.Items.Where(i => !i.IsDirectory)
                    .Count(i => File.Exists(Path.Combine(_destination, i.Path.Replace('/', '\\'))));
                if (existing > 0)
                {
                    ConflictText.Text = existing == 1
                        ? "Un fichier de l'archive existe déjà dans ce dossier."
                        : $"{existing} fichiers de l'archive existent déjà dans ce dossier.";
                    Show(ConflictPanel, 560, 200);
                    return;
                }
            }
            catch (PasswordRequiredException ex)
            {
                AskPassword(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                ShowResult(InfoBarSeverity.Error, "Impossible d'ouvrir l'archive", ex.Message);
                return;
            }
        }
        await StartExtractAsync(overwrite: true);
    }

    private async void Overwrite_Click(object sender, RoutedEventArgs e) => await StartExtractAsync(overwrite: true);

    private async void KeepExisting_Click(object sender, RoutedEventArgs e) => await StartExtractAsync(overwrite: false);

    private Task StartExtractAsync(bool overwrite)
    {
        _historyAction = "Extraction";
        _historyTarget = _destination;
        return RunAsync("Extraction en cours…", (progress, token) => ArchiveService.ExtractAsync(_archive, _password, _destination, _indexes, overwrite, progress, token), () =>
        {
            _resultPath = _destination;
            HistoryService.Add("Extraction", _destination, _indexes is { Count: > 0 } count ? $"{count} élément(s)" : "archive entière", true);
            _onDone?.Invoke();
            if (_autoClose)
            {
                // Fenêtre refermée d'elle-même (extraction lancée depuis l'Explorateur) : la bulle annonce la fin
                Notify.Show("NeoRar", $"Extraction terminée — {_destination}");
                Close();
            }
            else
            {
                ShowResult(InfoBarSeverity.Success, "Extraction terminée", _destination);
            }
        });
    }

    private void AskPassword(string message)
    {
        // En arrière-plan, la fenêtre revient : saisir le mot de passe réclame une action
        if (_backgrounded)
        {
            _backgrounded = false;
            RestoreWindow();
        }
        PasswordPrompt.Text = message == "Mot de passe incorrect." ? "Mot de passe incorrect. Réessayez :" : "Cette archive est protégée. Saisissez son mot de passe :";
        ExtractPasswordBox.Password = "";
        Show(PasswordPanel, 560, 200);
        ExtractPasswordBox.Focus(FocusState.Programmatic);
    }

    private async void PasswordOk_Click(object sender, RoutedEventArgs e)
    {
        if (ExtractPasswordBox.Password.Length == 0)
            return;
        _password = ExtractPasswordBox.Password;
        await PrepareExtractAsync();
    }

    private void ExtractPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            PasswordOk_Click(sender, e);
    }

    // ---------- Sauvegarde planifiée ----------

    /// <summary>
    /// Exécute une sauvegarde : depuis le planificateur Windows (--backup) ou le bouton « Exécuter ».
    /// <paramref name="autoClose"/> : la fenêtre se referme après la bulle de résultat (exécution planifiée).
    /// </summary>
    public static void ShowBackup(BackupJob job, bool autoClose = false, Action? onDone = null)
    {
        var window = new TaskWindow
        {
            _backup = job,
            _closeOnResult = autoClose,
            _historyAction = "Sauvegarde",
            _historyTarget = job.Destination,
            _onDone = onDone,
        };
        window.TitleText.Text = "Sauvegarde";
        window.SubtitleText.Text = job.Name;
        window.Activate();
        _ = window.RunBackupAsync();
    }

    private Task RunBackupAsync()
    {
        var job = _backup!;
        return RunAsync("Sauvegarde en cours…",
            (progress, token) => ArchiveService.CompressAsync(job.Sources, job.Destination, BackupService.OptionsFor(job), progress, token),
            () =>
            {
                BackupService.MarkRun(job, true);
                HistoryService.Add("Sauvegarde", job.Destination, "réussie", true);
                _resultPath = job.Destination;
                _onDone?.Invoke();
                ShowResult(InfoBarSeverity.Success, "Sauvegarde terminée", $"{job.Name} → {job.Destination}");
            });
    }

    // ---------- Exécution ----------

    private async Task RunAsync(string label, Func<IProgress<ProgressInfo>, CancellationToken, Task> work, Action onSuccess)
    {
        _cancel = new CancellationTokenSource();
        ProgressLabel.Text = label;
        BackgroundButton.IsEnabled = true;
        ProgressBar.IsIndeterminate = true;
        _runStarted = DateTime.Now;
        PercentText.Text = "⏱ 00:00";
        CancelButton.IsEnabled = true;
        Show(ProgressPanel, 560, 190);

        // Tant que la barre est animée (analyse, ou pourcentage non mesurable), le temps écoulé défile
        // à droite : l'utilisateur voit que l'opération avance au lieu d'un chiffre figé.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (ProgressBar.IsIndeterminate)
            {
                var s = (int)(DateTime.Now - _runStarted).TotalSeconds;
                PercentText.Text = $"⏱ {s / 60:00}:{s % 60:00}";
            }
        };
        timer.Start();

        var progress = new Progress<ProgressInfo>(s =>
        {
            if (s.Phase is not null)
                ProgressLabel.Text = s.Phase;
            if (s.Indeterminate)
            {
                ProgressBar.IsIndeterminate = true;
            }
            else
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = s.Percent;
                PercentText.Text = $"{s.Percent:0} %";
            }
        });

        var task = work(progress, _cancel.Token);
        _running = task;
        try
        {
            await task;
            _running = null; // avant onSuccess, qui peut fermer la fenêtre
            onSuccess();
        }
        catch (OperationCanceledException)
        {
            if (_closing)
                return;
            RecordFailure("annulée");
            ShowResult(InfoBarSeverity.Warning, "Opération annulée",
                _backup is not null ? "La sauvegarde n'a pas été créée."
                : _sources.Count > 0 ? "L'archive n'a pas été créée."
                : "Les fichiers déjà extraits sont restés dans le dossier de destination.");
        }
        catch (PasswordRequiredException ex)
        {
            AskPassword(ex.Message);
        }
        catch (Exception ex)
        {
            RecordFailure(ex.Message);
            ShowResult(InfoBarSeverity.Error, _backup is not null ? "Sauvegarde échouée" : "L'opération a échoué", ex.Message);
        }
        finally
        {
            _running = null;
            timer.Stop();
            BringBackIfBackgrounded();
        }
    }

    /// <summary>Note l'échec (ou l'annulation) dans l'historique et, s'il s'agit d'une sauvegarde, son issue.</summary>
    private void RecordFailure(string detail)
    {
        if (_backup is { } backup)
        {
            BackupService.MarkRun(backup, false);
            _onDone?.Invoke(); // la liste des sauvegardes peut afficher la nouvelle dernière exécution
        }
        if (_historyAction is { } action && _historyTarget is { } target)
            HistoryService.Add(action, target, detail, false);
    }

    /// <summary>
    /// L'opération continuée en arrière-plan est terminée : la fenêtre revient au premier plan et la bulle
    /// annonce le résultat (si la fenêtre n'a pas été fermée entre-temps).
    /// </summary>
    private void BringBackIfBackgrounded()
    {
        if (!_backgrounded || _closed)
            return;
        _backgrounded = false;
        Notify.Show("NeoRar", $"{_resultTitle} — {_resultMessage}");
        RestoreWindow();
    }

    /// <summary>Réduit NeoRar : l'opération continue et se termine par un retour de fenêtre et une notification.</summary>
    private void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        BackgroundButton.IsEnabled = false;
        _backgrounded = true;
        MinimizeWindow();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        ProgressLabel.Text = "Annulation…";
        _cancel?.Cancel();
    }

    private bool _closing;

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_running is not { } running || _closing)
        {
            _closed = true; // la fenêtre disparaît vraiment : rien à restaurer en fin d'opération
            return;
        }
        // Fermeture pendant l'opération : on l'annule proprement avant de fermer
        args.Cancel = true;
        _closing = true;
        _cancel?.Cancel();
        try
        {
            await running;
        }
        catch (Exception)
        {
        }
        Close();
    }

    // ---------- Affichage ----------

    private void Show(FrameworkElement panel, int width, int height)
    {
        foreach (var p in new FrameworkElement[] { FormPanel, PasswordPanel, ConflictPanel, ProgressPanel, ResultPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        SetSize(width, height);
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        _resultTitle = title;
        _resultMessage = message;
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ShowInFolderButton.Visibility = _resultPath is not null && severity == InfoBarSeverity.Success ? Visibility.Visible : Visibility.Collapsed;
        Show(ResultPanel, 560, 200);

        // Sauvegarde lancée par le planificateur : la bulle porte le résultat, pas de fenêtre orpheline
        if (_closeOnResult)
        {
            Notify.Show("NeoRar", $"{title} — {message}");
            Close();
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is null)
            return;
        var args = File.Exists(_resultPath) ? $"/select,\"{_resultPath}\"" : $"\"{_resultPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Réduit NeoRar dans la barre des tâches (MovePresenter n'existe pas dans cette version de WinUI).</summary>
    private void MinimizeWindow() => ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), SwMinimize);

    /// <summary>Sort NeoRar de la barre des tâches.</summary>
    private void RestoreWindow()
    {
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), SwRestore);
        Activate();
    }
}
