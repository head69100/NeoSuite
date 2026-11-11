using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NeoRar.Models;
using NeoRar.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace NeoRar;

/// <summary>
/// Fenêtre principale, comme celle de WinRAR : on parcourt le disque, on sélectionne des fichiers pour les
/// compresser, et une archive s'ouvre comme un dossier (« .. » à sa racine ramène au dossier qui la contient).
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Un élément du fil d'Ariane : dossier du disque, « Ce PC », ou dossier de l'archive ouverte.</summary>
    private sealed record Crumb(string Label, string? DiskFolder, string? ArchiveFolder, bool IsThisPc)
    {
        public override string ToString() => Label;
    }

    private readonly WindowMinSize _minSize;
    private readonly List<Crumb> _crumbs = [];

    // Disque : dossier affiché (null = « Ce PC », la liste des lecteurs)
    private string? _diskFolder;

    // Archive ouverte (null = on parcourt le disque)
    private ArchiveInfo? _archive;
    private string? _password;
    private string _folder = "";

    private bool InArchive => _archive is not null;

    public MainWindow(string? archiveToOpen = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1100, 720));
        _minSize = new WindowMinSize(this, 760, 480);
        SetTheme(AppSettings.Current.Theme);

        // Bouton « précédent » de la souris, et lasso de sélection (la liste garde ses clics pour elle : handledEventsToo)
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), handledEventsToo: true);
        EntryList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(EntryList_PointerPressed), handledEventsToo: true);
        EntryList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(EntryList_PointerMoved), handledEventsToo: true);
        EntryList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(EntryList_PointerReleased), handledEventsToo: true);
        EntryList.PointerCaptureLost += (_, _) => EndMarquee();

        RootGrid.Loaded += async (_, _) =>
        {
            if (archiveToOpen is not null)
            {
                _diskFolder = Path.GetDirectoryName(Path.GetFullPath(archiveToOpen));
                if (await OpenArchiveAsync(archiveToOpen))
                    return;
                NavigateDisk(_diskFolder);
                return;
            }
            var last = AppSettings.Current.LastFolder;
            NavigateDisk(last is not null && Directory.Exists(last)
                ? last
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        };
    }

    public void SetTheme(int theme) => RootGrid.RequestedTheme = theme switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    // ======================================================================
    // Disque
    // ======================================================================

    /// <summary>Affiche un dossier du disque (null = « Ce PC »), en sélectionnant éventuellement un élément.</summary>
    private void NavigateDisk(string? folder, string? select = null)
    {
        _archive = null;
        _password = null;
        _folder = "";
        MessageBar.IsOpen = false;

        var list = new List<ArchiveEntry>();
        try
        {
            if (folder is null)
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    var letter = drive.Name.TrimEnd('\\');
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Disque local" : drive.VolumeLabel;
                    list.Add(new ArchiveEntry { Name = $"{label} ({letter})", FullPath = drive.Name, DiskPath = drive.Name, IsFolder = true, IsDrive = true });
                }
            }
            else
            {
                var dir = new DirectoryInfo(folder);
                foreach (var d in dir.EnumerateDirectories().Where(IsShown).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                    list.Add(new ArchiveEntry { Name = d.Name, FullPath = d.FullName, DiskPath = d.FullName, IsFolder = true, Modified = d.LastWriteTime });
                foreach (var f in dir.EnumerateFiles().Where(IsShown).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                    list.Add(new ArchiveEntry { Name = f.Name, FullPath = f.FullName, DiskPath = f.FullName, IsFolder = false, Size = (ulong)f.Length, Modified = f.LastWriteTime });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ShowMessage(InfoBarSeverity.Error, "Dossier inaccessible", ex.Message);
            if (folder is not null && _diskFolder is not null && !string.Equals(folder, _diskFolder, StringComparison.OrdinalIgnoreCase))
                return; // on reste dans le dossier précédent
        }

        _diskFolder = folder;
        if (folder is not null)
        {
            AppSettings.Current.LastFolder = folder;
            AppSettings.Current.Save();
        }

        Title = "NeoRar";
        TitleArchiveText.Text = folder is null ? "· Ce PC" : "· " + folder;
        ShowEntries(list, select);
        BuildCrumbs();

        var folders = list.Count(e => e.IsFolder && !e.IsParent);
        var files = list.Count(e => !e.IsFolder);
        StatusText.Text = folder is null ? $"{folders} lecteur{(folders > 1 ? "s" : "")}"
            : $"{folders} dossier{(folders > 1 ? "s" : "")} · {files} fichier{(files > 1 ? "s" : "")}";
        EmptyText.Text = "Ce dossier est vide.";
        EmptyText.Visibility = folder is not null && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
    }

    private static bool IsShown(FileSystemInfo info) => (info.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) == 0;

    private void RefreshDisk(string? select = null)
    {
        if (!InArchive)
            NavigateDisk(_diskFolder, select);
    }

    // ======================================================================
    // Archive
    // ======================================================================

    /// <summary>Ouvre une archive (mot de passe demandé si besoin). Renvoie false si elle n'a pas pu l'être.</summary>
    private async Task<bool> OpenArchiveAsync(string path, string? password = null)
    {
        SetBusy(true);
        try
        {
            ArchiveInfo archive;
            while (true)
            {
                try
                {
                    archive = await ArchiveService.OpenAsync(path, password);
                    break;
                }
                catch (PasswordRequiredException ex)
                {
                    password = await AskPasswordAsync(Path.GetFileName(path), ex.Message);
                    if (password is null)
                        return false;
                }
            }

            _archive = archive;
            _password = password;
            _diskFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            AppSettings.Current.AddRecent(path);
            Title = $"{Path.GetFileName(path)} - NeoRar";
            TitleArchiveText.Text = "· " + Path.GetFileName(path);
            MessageBar.IsOpen = false;
            UpdateArchiveStatus();
            NavigateArchive("");
            return true;
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Impossible d'ouvrir l'archive", ex is FileNotFoundException ? "Le fichier est introuvable." : ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadArchiveAsync()
    {
        if (_archive is null)
            return;
        var folder = _folder;
        if (await OpenArchiveAsync(_archive.FilePath, _password) && folder.Length > 0
            && _archive!.Items.Any(i => i.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
            NavigateArchive(folder);
    }

    /// <summary>Affiche un dossier de l'archive ouverte (« » = sa racine).</summary>
    private void NavigateArchive(string folder, string? select = null)
    {
        if (_archive is null)
            return;
        _folder = folder;
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var entries = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _archive.Items)
        {
            if (!item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || item.Path.Length == prefix.Length)
                continue;
            var rest = item.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                // Élément plus profond : son dossier de premier niveau apparaît ici
                var name = rest[..slash];
                if (!entries.ContainsKey(name))
                    entries[name] = new ArchiveEntry { Name = name, FullPath = prefix + name, IsFolder = true };
            }
            else
            {
                entries[rest] = new ArchiveEntry
                {
                    Name = rest,
                    FullPath = item.Path,
                    IsFolder = item.IsDirectory,
                    Size = item.Size,
                    Modified = item.Modified,
                    Encrypted = item.Encrypted,
                    Index = item.Index,
                };
            }
        }

        var list = entries.Values
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ShowEntries(list, select);
        BuildCrumbs();
        EmptyText.Text = "Ce dossier de l'archive est vide.";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateArchiveStatus();
        UpdateCommands();
    }

    private void UpdateArchiveStatus()
    {
        if (_archive is null)
            return;
        var files = _archive.Items.Count(i => !i.IsDirectory);
        var parts = new List<string>
        {
            $"{files} fichier{(files > 1 ? "s" : "")}",
            // 7-Zip ne donne pas la taille totale pour tous les formats (RAR : -1) : on additionne les fichiers
            $"{ArchiveEntry.FormatSize(_archive.Items.Sum(i => (double)i.Size))} → {ArchiveEntry.FormatSize(_archive.ArchiveSize > 0 ? _archive.ArchiveSize : new FileInfo(_archive.FilePath).Length)}",
            FormatName(_archive.Format),
        };
        if (_archive.VolumeCount > 1)
            parts.Add($"{_archive.VolumeCount} tomes");
        if (_archive.IsSolid)
            parts.Add("solide");
        if (_archive.HasEncryptedFiles || _password is not null)
            parts.Add("protégée par mot de passe");
        StatusText.Text = string.Join(" · ", parts);
    }

    private static string FormatName(string format) => format switch
    {
        "SevenZip" => "7z",
        "Rar" => "RAR",
        "Rar4" => "RAR4",
        "Zip" => "ZIP",
        "GZip" => "GZip",
        "BZip2" => "BZip2",
        "Tar" => "TAR",
        "Iso" => "ISO",
        _ => format,
    };

    // ======================================================================
    // Liste, fil d'Ariane, remonter
    // ======================================================================

    private void ShowEntries(List<ArchiveEntry> list, string? select)
    {
        EntryList.ItemsSource = list;
        _ = LoadIconsAsync(list);
        if (select is not null && list.FirstOrDefault(e => string.Equals(e.DiskPath ?? e.FullPath, select, StringComparison.OrdinalIgnoreCase)) is { } target)
        {
            EntryList.SelectedItem = target;
            EntryList.ScrollIntoView(target);
        }
        UpdateSelection();
    }

    private static async Task LoadIconsAsync(List<ArchiveEntry> entries)
    {
        foreach (var entry in entries)
            entry.Icon = await FileIconService.GetAsync(entry.Name, entry.IsFolder);
    }

    private void BuildCrumbs()
    {
        _crumbs.Clear();
        _crumbs.Add(new Crumb("Ce PC", null, null, IsThisPc: true));
        if (_diskFolder is not null)
        {
            var root = Path.GetPathRoot(_diskFolder)!;
            _crumbs.Add(new Crumb(root.TrimEnd('\\'), root, null, false));
            var current = root;
            foreach (var part in _diskFolder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                _crumbs.Add(new Crumb(part, current, null, false));
            }
        }
        if (_archive is not null)
        {
            _crumbs.Add(new Crumb(Path.GetFileName(_archive.FilePath), null, "", false));
            var inner = "";
            foreach (var part in _folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                inner = inner.Length == 0 ? part : inner + "/" + part;
                _crumbs.Add(new Crumb(part, null, inner, false));
            }
        }
        PathBar.ItemsSource = _crumbs.ToList();
        UpButton.IsEnabled = InArchive || _diskFolder is not null;
    }

    private void PathBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _crumbs.Count)
            return;
        var crumb = _crumbs[args.Index];
        if (crumb.ArchiveFolder is { } inner)
            NavigateArchive(inner);
        else
            NavigateDisk(crumb.IsThisPc ? null : crumb.DiskFolder);
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) => GoUp();

    /// <summary>Remonte d'un niveau : dossier parent dans l'archive, puis dossier qui la contient, puis vers « Ce PC ».</summary>
    private void GoUp()
    {
        if (_archive is { } archive)
        {
            if (_folder.Length > 0)
            {
                var slash = _folder.LastIndexOf('/');
                var parent = slash < 0 ? "" : _folder[..slash];
                NavigateArchive(parent, select: _folder);
            }
            else
            {
                NavigateDisk(Path.GetDirectoryName(archive.FilePath), select: archive.FilePath);
            }
            return;
        }
        if (_diskFolder is null)
            return;
        var up = Directory.GetParent(_diskFolder)?.FullName;
        NavigateDisk(up, select: _diskFolder);
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed)
        {
            GoUp();
            e.Handled = true;
        }
    }

    private async void EntryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ArchiveEntry entry)
            await OpenEntryAsync(entry);
    }

    /// <summary>Dossier : on y entre. Archive du disque : on l'ouvre. Autre fichier : ouvert avec son programme habituel.</summary>
    private async Task OpenEntryAsync(ArchiveEntry entry)
    {
        if (entry.IsParent)
        {
            GoUp();
            return;
        }

        if (!InArchive)
        {
            if (entry.IsFolder)
                NavigateDisk(entry.DiskPath);
            else if (entry.IsArchiveFile)
                await OpenArchiveAsync(entry.DiskPath!);
            else
                OpenWithShell(entry.DiskPath!);
            return;
        }

        if (entry.IsFolder)
        {
            NavigateArchive(entry.FullPath);
            return;
        }
        if (entry.Index is not { } index)
            return;

        // Fichier de l'archive : extrait dans un dossier temporaire puis ouvert
        var temp = NewTempFolder();
        SetBusy(true);
        try
        {
            await ExtractWithPasswordAsync(temp, [index]);
            var file = Path.Combine(temp, entry.FullPath.Replace('/', '\\'));
            if (File.Exists(file))
                OpenWithShell(file);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, $"Impossible d'ouvrir {entry.Name}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, $"Impossible d'ouvrir {Path.GetFileName(path)}", ex.Message);
        }
    }

    private static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "NeoRar", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Extraction silencieuse (ouvrir ou glisser un fichier), avec demande du mot de passe si besoin.</summary>
    private async Task ExtractWithPasswordAsync(string destination, IReadOnlyCollection<int> indexes)
    {
        while (true)
        {
            try
            {
                await ArchiveService.ExtractAsync(_archive!.FilePath, _password, destination, indexes, true, null, CancellationToken.None);
                return;
            }
            catch (PasswordRequiredException ex)
            {
                _password = await AskPasswordAsync(Path.GetFileName(_archive!.FilePath), ex.Message) ?? throw new OperationCanceledException();
            }
        }
    }

    // ======================================================================
    // Sélection et commandes
    // ======================================================================

    private List<ArchiveEntry> SelectedEntries => EntryList.SelectedItems.OfType<ArchiveEntry>().Where(e => !e.IsParent).ToList();

    /// <summary>Fichiers et dossiers du disque sélectionnés (pas les lecteurs).</summary>
    private List<string> SelectedDiskPaths => SelectedEntries.Where(e => e.DiskPath is not null && !e.IsDrive).Select(e => e.DiskPath!).ToList();

    private List<string> SelectedDiskArchives => SelectedEntries.Where(e => e.IsArchiveFile).Select(e => e.DiskPath!).ToList();

    /// <summary>Index 7-Zip des éléments sélectionnés, dossiers compris avec tout leur contenu (null = toute l'archive).</summary>
    private IReadOnlyCollection<int>? SelectedIndexes()
    {
        var selected = SelectedEntries;
        if (_archive is null || selected.Count == 0)
            return _folder.Length == 0 ? null : IndexesUnder(_folder);
        return IndexesOf(selected);
    }

    private HashSet<int> IndexesOf(IEnumerable<ArchiveEntry> entries)
    {
        var indexes = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.Index is { } index)
                indexes.Add(index);
            if (entry.IsFolder)
                indexes.UnionWith(IndexesUnder(entry.FullPath));
        }
        return indexes;
    }

    private HashSet<int> IndexesUnder(string folder) =>
        _archive!.Items.Where(i => i.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Index).ToHashSet();

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = SelectedEntries;
        SelectionText.Text = selected.Count == 0 ? ""
            : $"{selected.Count} sélectionné{(selected.Count > 1 ? "s" : "")}"
              + (selected.Any(s => !s.IsFolder) ? $" · {ArchiveEntry.FormatSize(selected.Where(s => !s.IsFolder).Sum(s => (double)s.Size))}" : "");
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var selected = SelectedEntries;
        if (InArchive)
        {
            CompressButton.Visibility = Visibility.Collapsed;
            AddButton.Visibility = Visibility.Visible;
            DeleteButton.Visibility = Visibility.Visible;
            AddButton.IsEnabled = _archive!.CanModify;
            DeleteButton.IsEnabled = _archive.CanModify && selected.Count > 0;
            ExtractToButton.IsEnabled = ExtractFolderButton.IsEnabled = TestButton.IsEnabled = true;
            ExtractToButton.Label = selected.Count > 0 ? "Extraire la sélection…" : "Extraire vers…";
        }
        else
        {
            CompressButton.Visibility = Visibility.Visible;
            AddButton.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Collapsed;
            CompressButton.IsEnabled = SelectedDiskPaths.Count > 0;
            var archives = SelectedDiskArchives.Count;
            ExtractToButton.IsEnabled = ExtractFolderButton.IsEnabled = archives > 0;
            TestButton.IsEnabled = archives == 1;
            ExtractToButton.Label = "Extraire vers…";
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickArchiveAsync(this) is { } path)
            await OpenArchiveAsync(path);
    }

    /// <summary>Compresse la sélection du disque, comme « Ajouter » dans WinRAR.</summary>
    private void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedDiskPaths;
        if (paths.Count > 0)
            TaskWindow.ShowCreate(paths, created => DispatcherQueue.TryEnqueue(() => RefreshDisk(select: created)));
    }

    private async void ExtractToButton_Click(object sender, RoutedEventArgs e)
    {
        if (InArchive)
        {
            if (await Pickers.PickFolderAsync(this) is { } folder)
                TaskWindow.ShowExtract(_archive!.FilePath, folder, _password, SelectedIndexes(), checkConflicts: true);
            return;
        }

        var archives = SelectedDiskArchives;
        if (archives.Count == 0 || await Pickers.PickFolderAsync(this) is not { } target)
            return;
        foreach (var archive in archives)
        {
            // Plusieurs archives : chacune dans son propre dossier pour ne pas mélanger leurs fichiers
            var destination = archives.Count == 1 ? target : ArchiveService.UniquePath(Path.Combine(target, ArchiveService.FolderNameFor(archive)), isDirectory: true);
            TaskWindow.ShowExtract(archive, destination, checkConflicts: archives.Count == 1, onDone: () => DispatcherQueue.TryEnqueue(() => RefreshDisk()));
        }
    }

    private void ExtractFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var archives = InArchive ? new List<string> { _archive!.FilePath } : SelectedDiskArchives;
        foreach (var archive in archives)
        {
            var destination = ArchiveService.UniquePath(Path.Combine(Path.GetDirectoryName(archive)!, ArchiveService.FolderNameFor(archive)), isDirectory: true);
            TaskWindow.ShowExtract(archive, destination, InArchive ? _password : null, onDone: () => DispatcherQueue.TryEnqueue(() => RefreshDisk(select: destination)));
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archive is not { CanModify: true } archive)
            return;
        var files = await Pickers.PickFilesAsync(this);
        if (files.Count > 0)
            TaskWindow.ShowAdd(archive, files, _folder, _password, () => DispatcherQueue.TryEnqueue(async () => await ReloadArchiveAsync()));
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteSelectionAsync();

    private async Task DeleteSelectionAsync()
    {
        var selected = SelectedEntries;
        if (_archive is not { CanModify: true } archive || selected.Count == 0)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = selected.Count == 1 ? $"Supprimer « {selected[0].Name} » de l'archive ?" : $"Supprimer {selected.Count} éléments de l'archive ?",
            Content = "Ils seront retirés de l'archive définitivement.",
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        SetBusy(true);
        try
        {
            var items = archive.Items.Where(i => selected.Any(s => s.Index == i.Index)).ToList();
            // Dossiers déduits des chemins (sans entrée propre) : on supprime leur contenu
            foreach (var folder in selected.Where(s => s.IsFolder && s.Index is null))
                items.Add(new ArchiveItem(-1, folder.FullPath, true, 0, default, false));
            await ArchiveService.DeleteAsync(archive, items, _password);
            await ReloadArchiveAsync();
            HistoryService.Add("Suppression", Path.GetFileName(archive.FilePath),
                $"{selected.Count} élément{(selected.Count > 1 ? "s" : "")}", true);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Suppression impossible", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var path = InArchive ? _archive!.FilePath : SelectedDiskArchives.FirstOrDefault();
        if (path is null)
            return;
        var password = InArchive ? _password : null;

        // En archive, une sélection est vérifiée seule (dossiers compris, tout leur contenu) ; sinon l'archive entière
        IReadOnlyCollection<int>? indexes = InArchive && SelectedEntries.Count > 0 ? IndexesOf(SelectedEntries) : null;

        SetBusy(true);
        try
        {
            while (true)
            {
                try
                {
                    var result = await ArchiveService.TestAsync(path, password, indexes);
                    if (InArchive)
                        _password = password;
                    if (indexes is null)
                    {
                        ShowMessage(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                            result.Ok ? $"{Path.GetFileName(path)} est intacte" : $"{Path.GetFileName(path)} est endommagée",
                            result.Ok ? "Toutes les données ont été vérifiées, aucune erreur." : "Certaines données sont corrompues : l'extraction risque d'échouer pour une partie des fichiers.");
                        HistoryService.Add("Vérification", path, result.Ok ? "archive intacte" : "archive endommagée", result.Ok);
                    }
                    else if (result.Checked == 0)
                    {
                        ShowMessage(InfoBarSeverity.Informational, "Rien à vérifier dans la sélection",
                            "La sélection ne contient pas de fichier contrôlable (dossiers vides ou fichiers chiffrés sans mot de passe).");
                    }
                    else if (result.Ok)
                    {
                        ShowMessage(InfoBarSeverity.Success, $"{result.Checked} fichiers vérifiés",
                            "Aucune erreur : les empreintes CRC de tous les fichiers correspondent.");
                        HistoryService.Add("Vérification", path, $"{result.Checked} fichiers : aucun problème", true);
                    }
                    else
                    {
                        ShowMessage(InfoBarSeverity.Error, $"{result.Damaged.Count} fichier(s) endommagé(s) sur {result.Checked}",
                            string.Join(", ", result.Damaged.Take(5)) + (result.Damaged.Count > 5 ? "…" : ""));
                        HistoryService.Add("Vérification", path, $"{result.Damaged.Count} fichier(s) endommagé(s) sur {result.Checked}", false);
                    }
                    return;
                }
                catch (PasswordRequiredException ex)
                {
                    password = await AskPasswordAsync(Path.GetFileName(path), ex.Message);
                    if (password is null)
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Test impossible", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void EntryList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Clic droit sur une ligne non sélectionnée : elle devient la sélection
        if ((args.OriginalSource as FrameworkElement)?.DataContext is ArchiveEntry entry && !EntryList.SelectedItems.Contains(entry))
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItems.Add(entry);
        }
        var selected = SelectedEntries;
        if (selected.Count == 0)
            return;

        var menu = new MenuFlyout();
        if (selected.Count == 1)
            menu.Items.Add(Item("Ouvrir", "", async () => await OpenEntryAsync(selected[0])));

        if (InArchive)
        {
            menu.Items.Add(Item("Extraire vers…", "", () => ExtractToButton_Click(this, new RoutedEventArgs())));
            if (_archive!.CanModify)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Supprimer de l'archive", "", async () => await DeleteSelectionAsync()));
            }
        }
        else
        {
            if (SelectedDiskArchives.Count > 0)
            {
                menu.Items.Add(Item("Extraire à côté", "", () => ExtractFolderButton_Click(this, new RoutedEventArgs())));
                menu.Items.Add(Item("Extraire vers…", "", () => ExtractToButton_Click(this, new RoutedEventArgs())));
            }
            if (SelectedDiskPaths.Count > 0)
                menu.Items.Add(Item("Compresser…", "", () => CompressButton_Click(this, new RoutedEventArgs())));
        }

        if (args.TryGetPosition(EntryList, out var point))
            menu.ShowAt(EntryList, point);
        else
            menu.ShowAt(EntryList);
        args.Handled = true;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when SelectedEntries.Count == 1:
                await OpenEntryAsync(SelectedEntries[0]);
                e.Handled = true;
                break;
            case VirtualKey.Delete when InArchive:
                await DeleteSelectionAsync();
                e.Handled = true;
                break;
            case VirtualKey.F5:
                if (InArchive)
                    await ReloadArchiveAsync();
                else
                    RefreshDisk();
                e.Handled = true;
                break;
        }
    }

    // ======================================================================
    // Lasso de sélection
    // ======================================================================

    private Point? _marqueeOrigin;       // en coordonnées du contenu (défilement compris)
    private bool _marqueeActive;
    private HashSet<ArchiveEntry> _marqueeBase = [];
    private ScrollViewer? _scroller;

    /// <summary>
    /// Appui dans un espace vide de la liste : début d'un lasso (et désélection, comme l'Explorateur).
    /// Sur une ligne, la liste sélectionne ; l'icône et le nom servent aussi à glisser les fichiers.
    /// Prendre le pointeur ici, même sur une ligne, empêchait la sélection au simple clic.
    /// </summary>
    private void EntryList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(EntryList);
        if (!point.Properties.IsLeftButtonPressed || IsOnScrollBar(e.OriginalSource as DependencyObject))
            return;

        var ctrl = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);

        // Sur une ligne : un clic gauche simple la sélectionne toujours, même si l'appui part de la zone
        // de glisser (icône/nom), qui peut empêcher la liste de prendre la sélection d'elle-même.
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } row)
        {
            if (!ctrl && !shift && row.DataContext is ArchiveEntry entry && !EntryList.SelectedItems.Contains(entry))
            {
                EntryList.SelectedItems.Clear();
                EntryList.SelectedItems.Add(entry);
            }
            return;
        }

        _scroller ??= FindDescendant<ScrollViewer>(EntryList);
        var offset = _scroller?.VerticalOffset ?? 0;
        _marqueeOrigin = new Point(point.Position.X, point.Position.Y + offset);
        _marqueeActive = false;
        _marqueeBase = ctrl ? EntryList.SelectedItems.OfType<ArchiveEntry>().ToHashSet() : [];

        // Clic dans le vide : tout désélectionner, comme l'Explorateur
        if (!ctrl)
            EntryList.SelectedItems.Clear();

        EntryList.CapturePointer(e.Pointer);
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void EntryList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeOrigin is not { } origin)
            return;
        var position = e.GetCurrentPoint(EntryList).Position;
        var offset = _scroller?.VerticalOffset ?? 0;
        var current = new Point(position.X, position.Y + offset);
        if (!_marqueeActive && Math.Abs(current.X - origin.X) < 5 && Math.Abs(current.Y - origin.Y) < 5)
            return;
        _marqueeActive = true;

        // Défilement automatique quand le lasso touche le haut ou le bas de la liste
        if (_scroller is not null)
        {
            if (position.Y < 16)
                _scroller.ChangeView(null, Math.Max(0, offset - 24), null, true);
            else if (position.Y > EntryList.ActualHeight - 16)
                _scroller.ChangeView(null, offset + 24, null, true);
            offset = _scroller.VerticalOffset;
        }

        var top = Math.Min(origin.Y, current.Y);
        var bottom = Math.Max(origin.Y, current.Y);
        var left = Math.Clamp(Math.Min(origin.X, current.X), 0, EntryList.ActualWidth);
        var right = Math.Clamp(Math.Max(origin.X, current.X), 0, EntryList.ActualWidth);

        Canvas.SetLeft(Marquee, left);
        Canvas.SetTop(Marquee, Math.Max(0, top - offset));
        Marquee.Width = right - left;
        Marquee.Height = Math.Max(0, Math.Min(bottom - offset, EntryList.ActualHeight) - Math.Max(0, top - offset));
        Marquee.Visibility = Visibility.Visible;

        if (EntryList.ItemsSource is not List<ArchiveEntry> items)
            return;
        foreach (var item in items)
        {
            if (item.IsParent || EntryList.ContainerFromItem(item) is not FrameworkElement container)
                continue; // ligne hors écran (non générée) : inchangée
            var bounds = container.TransformToVisual(EntryList).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var itemTop = bounds.Y + offset;
            var hit = itemTop < bottom && itemTop + bounds.Height > top;
            var wanted = hit || _marqueeBase.Contains(item);
            var isSelected = EntryList.SelectedItems.Contains(item);
            if (wanted && !isSelected)
                EntryList.SelectedItems.Add(item);
            else if (!wanted && isSelected)
                EntryList.SelectedItems.Remove(item);
        }
    }

    private void EntryList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeOrigin is null)
            return;
        var wasActive = _marqueeActive;
        var selection = EntryList.SelectedItems.OfType<ArchiveEntry>().ToList();
        EntryList.ReleasePointerCapture(e.Pointer);
        EndMarquee();

        // La ligne sous le curseur se sélectionne seule au relâchement : on rétablit la sélection du lasso juste après
        if (wasActive)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                EntryList.SelectedItems.Clear();
                foreach (var item in selection)
                    EntryList.SelectedItems.Add(item);
            });
    }


    private void EndMarquee()
    {
        _marqueeOrigin = null;
        _marqueeActive = false;
        Marquee.Visibility = Visibility.Collapsed;
    }

    private static bool IsOnScrollBar(DependencyObject? source) => FindAncestor<ScrollBar>(source) is not null;

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
                return match;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } found)
                return found;
        }
        return null;
    }

    // ======================================================================
    // Glisser-déposer
    // ======================================================================

    private bool _draggingOut;

    /// <summary>
    /// Glisser l'icône ou le nom vers l'Explorateur ou le Bureau (avec le reste de la sélection s'il en fait partie).
    /// Fichiers du disque : copiés tels quels. Fichiers d'une archive : rien n'est extrait avant le dépôt ; la cible
    /// les demande alors, on les extrait dans un dossier temporaire et l'Explorateur les copie à l'endroit choisi.
    /// Seule cette zone fait glisser : le reste de la ligne sert au lasso (la liste elle-même ne fait rien glisser).
    /// </summary>
    private async void Handle_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArchiveEntry grabbed || grabbed.IsParent || grabbed.IsDrive)
        {
            e.Cancel = true;
            return;
        }
        if (!EntryList.SelectedItems.Contains(grabbed))
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItems.Add(grabbed);
        }
        var entries = SelectedEntries.Where(x => !x.IsDrive).ToList();

        if (!InArchive)
        {
            _draggingOut = true;
            var paths = entries.Select(x => (x.DiskPath!, x.IsFolder)).ToList();
            e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    request.SetData(await ToStorageItemsAsync(paths));
                }
                finally
                {
                    deferral.Complete();
                }
            });
            return;
        }

        var archive = _archive!;
        // Fichiers chiffrés sans mot de passe connu : impossible de le demander pendant le glisser
        if (archive.HasEncryptedFiles && _password is null)
        {
            e.Cancel = true;
            _password = await AskPasswordAsync(Path.GetFileName(archive.FilePath), "");
            if (_password is not null)
                ShowMessage(InfoBarSeverity.Informational, "Mot de passe enregistré", "Vous pouvez maintenant faire glisser les fichiers.");
            return;
        }

        _draggingOut = true;
        var password = _password;
        var indexes = IndexesOf(entries);
        e.AllowedOperations = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var temp = NewTempFolder();
                await ArchiveService.ExtractAsync(archive.FilePath, password, temp, indexes, true, null, CancellationToken.None);
                request.SetData(await ToStorageItemsAsync(entries.Select(x => (Path.Combine(temp, x.FullPath.Replace('/', '\\')), x.IsFolder))));
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => ShowMessage(InfoBarSeverity.Error, "Impossible de sortir les fichiers", ex.Message));
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private static async Task<List<IStorageItem>> ToStorageItemsAsync(IEnumerable<(string Path, bool IsFolder)> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var (path, isFolder) in paths)
        {
            if (isFolder && Directory.Exists(path))
                items.Add(await StorageFolder.GetFolderFromPathAsync(path));
            else if (File.Exists(path))
                items.Add(await StorageFile.GetFileFromPathAsync(path));
        }
        return items;
    }

    private void Handle_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _draggingOut = false;
        // Déplacés vers un autre dossier : la liste du disque doit refléter leur départ
        if (!InArchive && args.DropResult == DataPackageOperation.Move)
            RefreshDisk();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        // Fichiers qu'on est en train de sortir : on ne les réajoute pas en les lâchant ici
        if (_draggingOut || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _archive is { CanModify: true }
            ? (_folder.Length > 0 ? $"Ajouter dans {_folder.Split('/')[^1]}" : "Ajouter à l'archive")
            : "Compresser";
    }

    /// <summary>
    /// Dépôt de fichiers : dans une archive 7z/ZIP ouverte, ils y sont ajoutés (dans le dossier affiché) ;
    /// une archive seule est ouverte ; sinon on propose de créer une archive avec, comme WinRAR.
    /// </summary>
    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_draggingOut || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0)
            return;

        if (_archive is { CanModify: true } archive)
        {
            var where = _folder.Length > 0 ? $"dans « {_folder.Split('/')[^1]} » de {Path.GetFileName(archive.FilePath)}" : $"à {Path.GetFileName(archive.FilePath)}";
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Ajouter à l'archive ?",
                Content = paths.Count == 1 ? $"Ajouter « {Path.GetFileName(paths[0])} » {where} ?" : $"Ajouter {paths.Count} éléments {where} ?",
                PrimaryButtonText = "Ajouter",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                TaskWindow.ShowAdd(archive, paths, _folder, _password, () => DispatcherQueue.TryEnqueue(async () => await ReloadArchiveAsync()));
            return;
        }

        if (paths.Count == 1 && items[0] is StorageFile && ArchiveService.IsArchive(paths[0]))
        {
            await OpenArchiveAsync(paths[0]);
            return;
        }

        TaskWindow.ShowCreate(paths, created => DispatcherQueue.TryEnqueue(() => RefreshDisk(select: created)));
    }

    // ======================================================================
    // Paramètres
    // ======================================================================

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var theme = new ComboBox { Header = "Thème", MinWidth = 200, Items = { "Système", "Clair", "Sombre" }, SelectedIndex = settings.Theme };
        var shell = new ToggleSwitch
        {
            Header = "Intégration à l'Explorateur",
            IsOn = ShellIntegration.IsEnabled,
            OnContent = "Clic droit « Extraire ici », « Ajouter à une archive » et « Ouvrir avec »",
            OffContent = "Désactivée",
        };
        var hint = new TextBlock
        {
            Text = "Sous Windows 11, ces entrées sont dans « Afficher plus d'options ». Pour que le double-clic ouvre NeoRar, "
                 + "faites un clic droit sur une archive › Ouvrir avec › Choisir une autre application › NeoRar › Toujours.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var version = new TextBlock
        {
            Text = $"NeoRar {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · moteur 7-Zip",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Paramètres",
            Content = new StackPanel { Spacing = 16, Children = { theme, shell, hint, version } },
            PrimaryButtonText = "Enregistrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        settings.Theme = theme.SelectedIndex;
        settings.ShellIntegration = shell.IsOn;
        settings.Save();
        SetTheme(settings.Theme);
        try
        {
            if (shell.IsOn)
                ShellIntegration.Enable();
            else
                ShellIntegration.Disable();
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Intégration à l'Explorateur impossible", ex.Message);
        }
    }

    // ======================================================================
    // Historique
    // ======================================================================

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

        var panel = new StackPanel { Spacing = 8 };
        var entries = HistoryService.Entries;
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Aucune opération enregistrée pour l'instant : créations, extractions, vérifications, "
                     + "suppressions et sauvegardes apparaîtront ici (les 200 dernières sont conservées).",
                TextWrapping = TextWrapping.Wrap,
                Style = caption,
                Foreground = secondary,
            });
        }
        else
        {
            var list = new StackPanel { Spacing = 8 };
            foreach (var entry in entries.Take(50))
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock
                {
                    Text = $"{entry.When:dd/MM/yyyy HH:mm} · {entry.Action}",
                    Style = caption,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                row.Children.Add(new TextBlock
                {
                    Text = entry.Target + (entry.Detail.Length > 0 ? " — " + entry.Detail : ""),
                    Style = caption,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = entry.Ok ? secondary : critical,
                });
                list.Children.Add(row);
            }
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Historique des opérations",
            Content = panel,
            PrimaryButtonText = "Effacer",
            CloseButtonText = "Fermer",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            HistoryService.Clear();
    }

    // ======================================================================
    // Sauvegardes planifiées
    // ======================================================================

    private async void BackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowBackupsAsync())
            await ShowBackupEditorAsync();
    }

    /// <summary>
    /// Liste des sauvegardes planifiées : exécution immédiate, suppression (en deux clics), ou ouverture
    /// du formulaire de création. Renvoie true si l'utilisateur demande « Nouvelle sauvegarde… ».
    /// </summary>
    private async Task<bool> ShowBackupsAsync()
    {
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var list = new StackPanel { Spacing = 8 };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Chaque sauvegarde est enregistrée comme tâche Windows : à l'heure choisie (ou au prochain "
                         + "démarrage si le PC était éteint), NeoRar crée l'archive — l'ancienne version est remplacée — "
                         + "puis vous prévient par une notification. Exécution pour cet utilisateur, NeoRar doit rester installé.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = caption,
                    Foreground = secondary,
                },
                list,
            },
        };

        void Rebuild()
        {
            list.Children.Clear();
            if (BackupService.Jobs.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "Aucune sauvegarde planifiée pour le moment.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = caption,
                    Foreground = secondary,
                });
                return;
            }

            foreach (var job in BackupService.Jobs.ToList())
            {
                var texts = new StackPanel { Spacing = 1 };
                texts.Children.Add(new TextBlock
                {
                    Text = job.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                texts.Children.Add(new TextBlock
                {
                    Text = $"{job.ScheduleText} · {job.LastRunText}",
                    Style = caption,
                    Foreground = secondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                texts.Children.Add(new TextBlock
                {
                    Text = $"{job.Sources.Count} dossier(s) → {job.Destination}",
                    Style = caption,
                    Foreground = secondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var run = new Button { Content = "Exécuter", Padding = new Thickness(12, 4, 12, 4) };
                run.Click += (_, _) =>
                {
                    run.IsEnabled = false;
                    TaskWindow.ShowBackup(job, onDone: () => DispatcherQueue.TryEnqueue(Rebuild));
                };

                var remove = new Button { Content = "Supprimer", Padding = new Thickness(12, 4, 12, 4) };
                var armed = false;
                remove.Click += (_, _) =>
                {
                    if (!armed)
                    {
                        armed = true;
                        remove.Content = "Confirmer la suppression";
                        return;
                    }
                    BackupService.Remove(job);
                    Rebuild();
                };

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { run, remove },
                };

                var grid = new Grid { ColumnSpacing = 10 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(texts, 0);
                Grid.SetColumn(buttons, 1);
                grid.Children.Add(texts);
                grid.Children.Add(buttons);

                list.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                    Child = grid,
                });
            }
        }
        Rebuild();

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Sauvegardes planifiées",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
            PrimaryButtonText = "Nouvelle sauvegarde…",
            CloseButtonText = "Fermer",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Formulaire d'une sauvegarde : dossiers sources, archive de destination, format et planification.</summary>
    private async Task ShowBackupEditorAsync()
    {
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        ContentDialog? dialog = null;
        var sources = new List<string>();

        var nameBox = new TextBox { Header = "Nom de la sauvegarde", PlaceholderText = "Documents" };
        var destBox = new TextBox { Header = "Archive de destination", PlaceholderText = @"D:\Sauvegardes\Documents.7z" };
        var formatCombo = new ComboBox
        {
            Header = "Format",
            SelectedIndex = 0,
            Items = { "7z · meilleure compression", "ZIP · s'ouvre partout" },
        };
        var levelCombo = new ComboBox
        {
            Header = "Compression",
            SelectedIndex = 3,
            Items = { "Stockage (aucune)", "La plus rapide", "Rapide", "Normale", "Élevée", "Ultra (plus lente)" },
        };
        var freqCombo = new ComboBox
        {
            Header = "Fréquence",
            SelectedIndex = 0,
            Items = { "Quotidienne", "Hebdomadaire" },
        };
        var dayCombo = new ComboBox
        {
            Header = "Jour",
            SelectedIndex = (int)DayOfWeek.Monday,
            IsEnabled = false,
            Items = { "Dimanche", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi" },
        };
        freqCombo.SelectionChanged += (_, _) => dayCombo.IsEnabled = freqCombo.SelectedIndex == 1;
        var timePicker = new TimePicker { Header = "Heure d'exécution", MinuteIncrement = 15, Time = new TimeSpan(20, 0, 0) };

        var sourcesList = new TextBlock
        {
            Text = "Aucun dossier choisi.",
            Style = caption,
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
        };

        void RefreshSources()
        {
            sourcesList.Text = sources.Count == 0
                ? "Aucun dossier choisi."
                : string.Join(Environment.NewLine, sources.Select(s => "• " + s));
        }

        void Check()
        {
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0 && sources.Count > 0
                    && Path.IsPathRooted(destBox.Text.Trim());
        }

        var addSource = new Button { Content = "Ajouter un dossier…" };
        var removeSource = new Button { Content = "Retirer le dernier", IsEnabled = false };
        addSource.Click += async (_, _) =>
        {
            if (await Pickers.PickFolderAsync(this) is { } folder)
            {
                if (!sources.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    sources.Add(folder);
                RefreshSources();
                removeSource.IsEnabled = sources.Count > 0;
                Check();
            }
        };
        removeSource.Click += (_, _) =>
        {
            if (sources.Count > 0)
            {
                sources.RemoveAt(sources.Count - 1);
                RefreshSources();
                removeSource.IsEnabled = sources.Count > 0;
                Check();
            }
        };

        var browseDest = new Button { Content = "Parcourir…", VerticalAlignment = VerticalAlignment.Bottom };
        browseDest.Click += async (_, _) =>
        {
            var kind = formatCombo.SelectedIndex == 1 ? ArchiveKind.Zip : ArchiveKind.SevenZip;
            var baseName = destBox.Text.Trim().Length > 0 ? Path.GetFileNameWithoutExtension(destBox.Text.Trim())
                : nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "Sauvegarde";
            if (await Pickers.PickSaveArchiveAsync(this, baseName + ArchiveService.Extension(kind), kind) is { } path)
            {
                destBox.Text = path;
                Check();
            }
        };

        var destGrid = new Grid { ColumnSpacing = 8 };
        destGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(destBox, 0);
        Grid.SetColumn(browseDest, 1);
        destGrid.Children.Add(destBox);
        destGrid.Children.Add(browseDest);

        var formatLevelGrid = new Grid { ColumnSpacing = 12 };
        formatLevelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formatLevelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formatLevelGrid.Children.Add(formatCombo);
        Grid.SetColumn(levelCombo, 1);
        formatLevelGrid.Children.Add(levelCombo);

        var planGrid = new Grid { ColumnSpacing = 12 };
        planGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        planGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        planGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        planGrid.Children.Add(freqCombo);
        Grid.SetColumn(dayCombo, 1);
        planGrid.Children.Add(dayCombo);
        Grid.SetColumn(timePicker, 2);
        planGrid.Children.Add(timePicker);

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                nameBox,
                new TextBlock { Text = "Dossiers à sauvegarder", Style = caption },
                sourcesList,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { addSource, removeSource },
                },
                destGrid,
                formatLevelGrid,
                planGrid,
                new TextBlock
                {
                    Text = "Pas de mot de passe : la tâche tourne sans interaction. À chaque exécution, l'archive "
                         + "existante est remplacée par la nouvelle version.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = caption,
                    Foreground = secondary,
                },
            },
        };

        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Nouvelle sauvegarde",
            Content = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content },
            PrimaryButtonText = "Enregistrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        nameBox.TextChanged += (_, _) => Check();
        destBox.TextChanged += (_, _) => Check();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var destination = destBox.Text.Trim();
        var job = new BackupJob
        {
            Name = nameBox.Text.Trim(),
            Sources = sources.ToList(),
            Destination = destination,
            Kind = formatCombo.SelectedIndex == 1 ? ArchiveKind.Zip : ArchiveKind.SevenZip,
            Level = Math.Clamp(levelCombo.SelectedIndex, 0, TaskWindow.Levels.Length - 1),
            Frequency = freqCombo.SelectedIndex == 1 ? BackupFrequency.Weekly : BackupFrequency.Daily,
            Hour = timePicker.Time.Hours,
            Minute = timePicker.Time.Minutes,
            Day = (DayOfWeek)Math.Clamp(dayCombo.SelectedIndex, 0, 6),
        };

        try
        {
            if (Path.GetDirectoryName(destination) is { } folder)
                Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            // Dossier inaccessible : la première exécution affichera l'erreur précise
        }

        try
        {
            BackupService.Add(job);
            ShowMessage(InfoBarSeverity.Success, "Sauvegarde planifiée", $"{job.Name} · {job.ScheduleText} (tâche Windows enregistrée).");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Tâche Windows non créée",
                $"La sauvegarde est enregistrée dans NeoRar, mais Windows n'a pas pu planifier son exécution : {ex.Message}");
        }
    }

    // ======================================================================
    // Utilitaires
    // ======================================================================

    private async Task<string?> AskPasswordAsync(string archiveName, string message)
    {
        var box = new PasswordBox { PlaceholderText = "Mot de passe" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = archiveName,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message == "Mot de passe incorrect." ? "Mot de passe incorrect. Réessayez :" : "Cette archive est protégée. Saisissez son mot de passe :", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Valider",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        EntryList.IsEnabled = !busy;
    }
}
