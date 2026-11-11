using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MonoTorrent;
using MonoTorrent.Client;

namespace NeoTorrent;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<TorrentItem> _torrents = new();
    private readonly ClientEngine _engine = new(new EngineSettingsBuilder().ToSettings());
    private string _downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public MainWindow(string? initialPath)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TorrentList.ItemsSource = _torrents;
        EmptyText.Visibility = _torrents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Directory.CreateDirectory(_downloadDir);
        _ = _engine.StartAllAsync();

        if (!string.IsNullOrEmpty(initialPath))
            _ = AddTorrentAsync(initialPath);
    }

    private void AddTorrentButton_Click(object sender, RoutedEventArgs e) => _ = PickTorrentAsync();
    private void AddMagnetButton_Click(object sender, RoutedEventArgs e) => _ = AddMagnetAsync();
    private void ChooseDownloadDirButton_Click(object sender, RoutedEventArgs e) => _ = PickDownloadDirAsync();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) { }
    private void StartButton_Click(object sender, RoutedEventArgs e) { if (TorrentList.SelectedItem is TorrentItem item) _ = item.Manager.StartAsync(); }
    private void PauseButton_Click(object sender, RoutedEventArgs e) { if (TorrentList.SelectedItem is TorrentItem item) _ = item.Manager.StopAsync(); }
    private void RemoveButton_Click(object sender, RoutedEventArgs e) { }

    private void TorrentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = TorrentList.SelectedItem != null;
        StartButton.IsEnabled = has;
        PauseButton.IsEnabled = has;
        RemoveButton.IsEnabled = has;
    }

    private async Task PickTorrentAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".torrent");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await AddTorrentAsync(file.Path);
    }

    private async Task AddTorrentAsync(string path)
    {
        try
        {
            var manager = await _engine.AddAsync(await Torrent.LoadAsync(path), _downloadDir);
            var item = new TorrentItem { Manager = manager, Name = manager.Torrent?.Name ?? Path.GetFileNameWithoutExtension(path) };
            manager.TorrentStateChanged += (_, __) => DispatcherQueue.TryEnqueue(Update);
            manager.PieceHashed += (_, __) => DispatcherQueue.TryEnqueue(Update);
            _torrents.Add(item);
            EmptyText.Visibility = Visibility.Collapsed;
            await manager.StartAsync();
            StatusText.Text = "Torrent ajouté";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task AddMagnetAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Ajouter un lien magnet",
            PrimaryButtonText = "Ajouter",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        var box = new TextBox { PlaceholderText = "magnet:?" };
        dialog.Content = box;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            try
            {
                var magnet = MagnetLink.Parse(box.Text.Trim());
                var manager = await _engine.AddAsync(magnet, _downloadDir);
                var item = new TorrentItem { Manager = manager, Name = magnet.Name ?? magnet.InfoHashes.V1OrV2.ToHex() };
                manager.TorrentStateChanged += (_, __) => DispatcherQueue.TryEnqueue(Update);
                manager.PieceHashed += (_, __) => DispatcherQueue.TryEnqueue(Update);
                _torrents.Add(item);
                EmptyText.Visibility = Visibility.Collapsed;
                await manager.StartAsync();
                StatusText.Text = "Lien magnet ajouté";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }
    }

    private async Task PickDownloadDirAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _downloadDir = folder.Path;
            StatusText.Text = "Dossier de téléchargement : " + _downloadDir;
        }
    }

    private void Update()
    {
        foreach (var item in _torrents)
        {
            var m = item.Manager;
            item.Progress = m.Progress;
            item.DownloadSpeed = m.Monitor.DownloadSpeed;
            item.UploadSpeed = m.Monitor.UploadSpeed;
            item.State = m.State;
            item.StatusText = GetStateText(m.State);
            item.ProgressText = $"{item.Progress * 100:0.0} %";
            item.SpeedText = $"↓ {FormatSpeed(item.DownloadSpeed)}  ↑ {FormatSpeed(item.UploadSpeed)}";
            item.EtaText = GetEta(m);
        }
        TorrentList.ItemsSource = null;
        TorrentList.ItemsSource = _torrents;
    }

    private static string GetStateText(TorrentState state) => state switch
    {
        TorrentState.Downloading => "Téléchargement",
        TorrentState.Seeding => "Partage",
        TorrentState.Paused => "En pause",
        TorrentState.Stopped => "Arrêté",
        TorrentState.Hashing => "Vérification",
        TorrentState.Metadata => "Récupération des métadonnées",
        TorrentState.Error => "Erreur",
        _ => state.ToString()
    };

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return bytesPerSecond + " o/s";
        if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024.0).ToString("0.0") + " Ko/s";
        if (bytesPerSecond < 1024 * 1024 * 1024) return (bytesPerSecond / 1024.0 / 1024.0).ToString("0.1") + " Mo/s";
        return (bytesPerSecond / 1024.0 / 1024.0 / 1024.0).ToString("0.2") + " Go/s";
    }

    private static string GetEta(TorrentManager m)
    {
        if (m.State != TorrentState.Downloading || m.Monitor.DownloadSpeed == 0) return "";
        var size = m.Torrent?.Size ?? 0;
        var remaining = size - m.Monitor.DataBytesDownloaded;
        var seconds = remaining / m.Monitor.DownloadSpeed;
        if (seconds < 60) return $"~{seconds:0}s restantes";
        if (seconds < 3600) return $"~{seconds / 60:0} min restantes";
        return $"~{seconds / 3600:0} h {(seconds % 3600) / 60:0} min restantes";
    }

    public class TorrentItem : System.ComponentModel.INotifyPropertyChanged
    {
        public TorrentManager Manager { get; set; } = null!;
        public string Name { get; set; } = "";
        public double Progress { get; set; }
        public long DownloadSpeed { get; set; }
        public long UploadSpeed { get; set; }
        public TorrentState State { get; set; }
        public string StatusText { get; set; } = "";
        public string ProgressText { get; set; } = "";
        public string SpeedText { get; set; } = "";
        public string EtaText { get; set; } = "";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
