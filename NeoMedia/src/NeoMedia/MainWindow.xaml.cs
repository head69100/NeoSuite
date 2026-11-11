using System.Collections.ObjectModel;
using System.Diagnostics;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NeoMedia.Models;
using NeoMedia.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace NeoMedia;

/// <summary>
/// Le lecteur : moteur de VLC (libVLC) dans une fenêtre WinUI. Les événements de libVLC arrivent sur ses propres
/// threads : tout passe par <see cref="Ui"/> avant de toucher à l'interface ou de relancer une lecture.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly float[] Speeds = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f];

    private readonly AppSettings _settings = AppSettings.Current;
    private readonly WindowMinSize _minSize;
    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _osdTimer;
    private List<string> _pending;

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private Media? _media;
    private PlaylistItem? _current;

    private long _resumeFrom;
    private bool _updatingSeek;
    private bool _pointerOnControls;
    private bool _fullscreen;
    private float _rate = 1f;
    private string _aspect = "";

    public MainWindow(List<string> files)
    {
        InitializeComponent();
        _pending = files;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1280, 760));
        _minSize = new WindowMinSize(this, 640, 420);

        PlaylistView.ItemsSource = _playlist;
        _playlist.CollectionChanged += (_, _) => UpdatePlaylistCount();
        UpdatePlaylistCount();

        VolumeSlider.Value = _settings.Volume;
        UpdateVolumeUi();
        UpdateRepeatUi();
        BuildSpeedMenu();
        ShowRecent();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => TryHideControls();

        _osdTimer = DispatcherQueue.CreateTimer();
        _osdTimer.Interval = TimeSpan.FromSeconds(1.2);
        _osdTimer.IsRepeating = false;
        _osdTimer.Tick += (_, _) => Osd.Visibility = Visibility.Collapsed;

        // Raccourcis interceptés avant les contrôles (sinon le curseur de volume mangerait les flèches)
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;
        Closed += MainWindow_Closed;
        Diag.Log("MainWindow construite (XAML charge)");
    }

    // ======================================================================
    // Moteur
    // ======================================================================

    /// <summary>La surface vidéo est prête : création du moteur de VLC, puis lecture des fichiers reçus au lancement.</summary>
    private void VideoView_Initialized(object? sender, InitializedEventArgs e)
    {
        Diag.Log("VideoView initialisee : creation du moteur");
        _libVLC = new LibVLC(false, [.. e.SwapChainOptions, "--no-video-title-show"]);
        _player = new MediaPlayer(_libVLC) { EnableHardwareDecoding = true };
        VideoView.MediaPlayer = _player;
        _player.Volume = _settings.Volume;
        _player.Mute = _settings.Muted;
        Diag.Log("moteur et lecteur crees");

        _player.Playing += (_, _) => Ui(OnPlaying);
        _player.Paused += (_, _) => Ui(() => SetPlayingUi(false));
        _player.Stopped += (_, _) => Ui(OnStopped);
        _player.EndReached += (_, _) => Ui(OnEnded);
        _player.EncounteredError += (_, _) => Ui(OnError);
        _player.TimeChanged += (_, args) => Ui(() => OnTime(args.Time));
        _player.LengthChanged += (_, args) => Ui(() => OnLength(args.Length));
        _player.Vout += (_, args) => Ui(() =>
        {
            Diag.Log($"Vout : {args.Count} sortie(s) video");
            SetVideoVisible(args.Count > 0);
        });

        if (_pending.Count > 0)
        {
            LoadPlaylist(_pending, play: true);
            _pending = [];
        }
    }

    private void Ui(Action action) => DispatcherQueue.TryEnqueue(() => action());

    // ======================================================================
    // Liste de lecture
    // ======================================================================

    /// <summary>Remplace la liste par ces fichiers (comme VLC à l'ouverture), ou les ajoute à la suite.</summary>
    private void LoadPlaylist(IReadOnlyList<string> sources, bool play, bool append = false)
    {
        if (sources.Count == 0)
            return;
        if (!append)
        {
            SaveCurrentPosition();
            _playlist.Clear();
            _current = null;
        }
        var first = _playlist.Count;
        foreach (var source in sources)
            _playlist.Add(new PlaylistItem(source));
        if (play)
            Play(_playlist[first]);
    }

    private void Play(PlaylistItem item)
    {
        if (_player is null || _libVLC is null)
        {
            Diag.Log($"lecture en attente du moteur : {item.Name}");
            _pending = [.. _playlist.Select(p => p.Source)];
            return;
        }

        SaveCurrentPosition();
        if (_current is not null)
            _current.IsCurrent = false;
        _current = item;
        item.IsCurrent = true;
        PlaylistView.ScrollIntoView(item);

        var media = item.IsFile ? new Media(_libVLC, item.Source, FromType.FromPath) : new Media(_libVLC, new Uri(item.Source));
        var previous = _media;
        _media = media;
        _resumeFrom = item.IsFile && _settings.ResumePlayback && _settings.ResumePositions.TryGetValue(item.Source, out var position) ? position : 0;

        Diag.Log($"lecture demandee : {item.Name}");
        _player.Play(media);
        previous?.Dispose();
        if (_rate != 1f)
            _player.SetRate(_rate);

        Title = $"{item.Name} - NeoMedia";
        TitleMediaText.Text = "· " + item.Name;
        WelcomePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        ErrorBar.IsOpen = false;
        ResumeBar.IsOpen = false;
        TimeText.Text = "0:00";
        LengthText.Text = "0:00";

        if (item.IsFile)
        {
            _settings.AddRecent(item.Source);
            _settings.Save();
        }
        _ = LoadMetadataAsync(media, item);
        ShowControls();
    }

    private void PlayNext(bool userAction)
    {
        if (_current is null)
            return;
        var index = _playlist.IndexOf(_current);
        if (index + 1 < _playlist.Count)
            Play(_playlist[index + 1]);
        else if (userAction || _settings.Repeat == RepeatMode.All)
        {
            if (_playlist.Count > 0 && (_settings.Repeat == RepeatMode.All || userAction && _playlist.Count > 1))
                Play(_playlist[0]);
        }
    }

    private void PlayPrevious()
    {
        if (_current is null || _player is null)
            return;
        // Comme partout : au-delà de 3 secondes, « précédent » revient au début du morceau en cours
        if (_player.Time > 3000)
        {
            _player.Time = 0;
            return;
        }
        var index = _playlist.IndexOf(_current);
        if (index > 0)
            Play(_playlist[index - 1]);
        else
            _player.Time = 0;
    }

    private void UpdatePlaylistCount() =>
        PlaylistCount.Text = _playlist.Count == 1 ? "1 élément" : $"{_playlist.Count} éléments";

    private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem item)
            Play(item);
    }

    private void PlaylistView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            foreach (var item in PlaylistView.SelectedItems.OfType<PlaylistItem>().ToList())
                _playlist.Remove(item);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && PlaylistView.SelectedItem is PlaylistItem item)
        {
            Play(item);
            e.Handled = true;
        }
    }

    private void PlaylistToggle_Click(object sender, RoutedEventArgs e) => TogglePlaylist();

    private void TogglePlaylist()
    {
        var show = PlaylistPanel.Visibility != Visibility.Visible;
        PlaylistPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Les commandes restent visibles à côté de la liste
        ControlsPanel.Margin = new Thickness(0, 0, show ? PlaylistPanel.Width : 0, 0);
        ShowControls();
    }

    private async void PlaylistAdd_Click(object sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync();
        if (files.Count > 0)
            LoadPlaylist(files, play: _current is null, append: true);
    }

    private void PlaylistClear_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPosition();
        _player?.Stop();
        _playlist.Clear();
        _current = null;
        ShowWelcome();
    }

    // ======================================================================
    // Événements de lecture
    // ======================================================================

    private void OnPlaying()
    {
        Diag.Log("Playing : lecture demarree par le moteur");
        SetPlayingUi(true);
        if (_player is null)
            return;
        SeekSlider.IsEnabled = _player.IsSeekable;

        if (_resumeFrom > 0 && _player.IsSeekable)
        {
            _player.Time = _resumeFrom;
            ResumeBar.Title = $"Reprise à {MediaFiles.FormatTime(_resumeFrom)}";
            ResumeBar.Message = "Là où vous vous étiez arrêté.";
            ResumeBar.IsOpen = true;
            _resumeFrom = 0;
        }

        // Pas de piste vidéo (musique) : pochette et titre à la place de l'écran noir
        var media = _media;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(600);
            if (_media == media && _player is { VideoTrackCount: <= 0 })
                SetVideoVisible(false);
        });
    }

    private void OnStopped()
    {
        SetPlayingUi(false);
        TimeText.Text = "0:00";
        SetSeek(0);
    }

    /// <summary>Fin du fichier : on l'oublie dans les reprises, puis suivant selon le mode de répétition.</summary>
    private void OnEnded()
    {
        if (_current is { IsFile: true } finished)
        {
            _settings.ResumePositions.Remove(finished.Source);
            _settings.Save();
        }
        if (_settings.Repeat == RepeatMode.One && _current is not null)
        {
            Play(_current);
            return;
        }
        var index = _current is null ? -1 : _playlist.IndexOf(_current);
        if (index >= 0 && index + 1 < _playlist.Count || _settings.Repeat == RepeatMode.All)
            PlayNext(userAction: false);
        else
            SetPlayingUi(false);
    }

    private void OnError()
    {
        Diag.Log("Erreur de lecture signalee par le moteur");
        ErrorBar.Title = "Lecture impossible";
        ErrorBar.Message = _current is null ? "" : $"« {_current.Name} » est illisible, endommagé ou introuvable.";
        ErrorBar.IsOpen = true;
        SetPlayingUi(false);
        // Liste de lecture : on passe au suivant, comme VLC
        if (_current is not null && _playlist.IndexOf(_current) + 1 < _playlist.Count)
            PlayNext(userAction: false);
    }

    private void OnTime(long time)
    {
        TimeText.Text = MediaFiles.FormatTime(time);
        SetSeek(time / 1000.0);
    }

    private void OnLength(long length)
    {
        LengthText.Text = MediaFiles.FormatTime(length);
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, length / 1000.0);
        _updatingSeek = false;
        if (_current is not null && length > 0)
            _current.Duration = MediaFiles.FormatTime(length);
    }

    private void SetSeek(double seconds)
    {
        _updatingSeek = true;
        SeekSlider.Value = Math.Min(seconds, SeekSlider.Maximum);
        _updatingSeek = false;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _player is not { IsSeekable: true })
            return;
        _player.Time = (long)(e.NewValue * 1000);
        TimeText.Text = MediaFiles.FormatTime(_player.Time);
        ResumeBar.IsOpen = false;
    }

    private void SetPlayingUi(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "" : "";
        if (playing)
            RestartHideTimer();
        else
            ShowControls(autoHide: false);
    }

    private void SetVideoVisible(bool hasVideo)
    {
        AudioPanel.Visibility = hasVideo || _current is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Titre, artiste et pochette (lus dans le fichier) pour l'affichage des musiques.</summary>
    private async Task LoadMetadataAsync(Media media, PlaylistItem item)
    {
        AudioTitle.Text = item.Name;
        AudioArtist.Text = "";
        ArtworkImage.Source = null;

        // Analyse réservée à la musique : titre, artiste et pochette. Sur une vidéo, elle n'apporte
        // rien (l'écran vidéo remplace ce panneau) et l'analyse du fichier alourdit l'ouverture.
        var extension = item.IsFile ? Path.GetExtension(item.Source) : "";
        if (MediaFiles.VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            Diag.Log("video : analyse de metadonnees sautee (duree fournie par le lecteur)");
            return;
        }

        try
        {
            await media.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.FetchLocal, 5000);
        }
        catch (Exception)
        {
            return;
        }
        if (_media != media)
            return;

        if (media.Meta(MetadataType.Title) is { Length: > 0 } title)
            AudioTitle.Text = title;
        var artist = media.Meta(MetadataType.Artist);
        var album = media.Meta(MetadataType.Album);
        AudioArtist.Text = string.Join(" · ", new[] { artist, album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (media.Meta(MetadataType.ArtworkURL) is { Length: > 0 } artwork && Uri.TryCreate(artwork, UriKind.Absolute, out var uri))
            ArtworkImage.Source = new BitmapImage(uri);
        if (media.Duration > 0)
            item.Duration = MediaFiles.FormatTime(media.Duration);
    }

    private void RestartFromBeginning_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
            _player.Time = 0;
        ResumeBar.IsOpen = false;
    }

    // ======================================================================
    // Commandes
    // ======================================================================

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_player is null)
            return;
        if (_player.IsPlaying)
            _player.Pause();
        else if (_player.State is VLCState.Paused)
            _player.Play();
        else if (_current is not null)
            Play(_current); // arrêté ou terminé : on relance
        else if (_playlist.Count > 0)
            Play(_playlist[0]);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPosition();
        _player?.Stop();
    }

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext(userAction: true);

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void Seek(long deltaMs)
    {
        if (_player is not { IsSeekable: true })
            return;
        var target = Math.Clamp(_player.Time + deltaMs, 0, Math.Max(0, _player.Length - 500));
        _player.Time = target;
        ShowOsd($"{(deltaMs > 0 ? "»" : "«")} {MediaFiles.FormatTime(target)} / {MediaFiles.FormatTime(_player.Length)}");
    }

    // --- Volume ---

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var volume = (int)Math.Round(e.NewValue);
        _settings.Volume = volume;
        if (_player is not null)
        {
            _player.Volume = volume;
            if (volume > 0 && _player.Mute)
                SetMute(false);
        }
        UpdateVolumeUi();
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 150);
        ShowOsd($"Volume {(int)VolumeSlider.Value} %");
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => SetMute(!_settings.Muted);

    private void SetMute(bool mute)
    {
        _settings.Muted = mute;
        if (_player is not null)
            _player.Mute = mute;
        UpdateVolumeUi();
        ShowOsd(mute ? "Son coupé" : $"Volume {_settings.Volume} %");
    }

    private void UpdateVolumeUi()
    {
        var v = _settings.Volume;
        VolumeIcon.Glyph = _settings.Muted || v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        VolumeText.Text = _settings.Muted ? "muet" : $"{v} %";
    }

    // --- Vitesse ---

    private void BuildSpeedMenu()
    {
        SpeedMenu.Items.Clear();
        foreach (var speed in Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = SpeedLabel(speed), GroupName = "speed", IsChecked = Math.Abs(speed - _rate) < 0.01 };
            item.Click += (_, _) => SetRate(speed);
            SpeedMenu.Items.Add(item);
        }
    }

    private static string SpeedLabel(float speed) => speed == 1f ? "Normale (1×)" : $"{speed:0.##}×";

    private void SetRate(float rate)
    {
        _rate = rate;
        _player?.SetRate(rate);
        SpeedText.Text = $"{rate:0.##}×";
        BuildSpeedMenu();
        ShowOsd($"Vitesse {rate:0.##}×");
    }

    private void StepRate(int direction)
    {
        var index = Array.FindIndex(Speeds, s => Math.Abs(s - _rate) < 0.01);
        index = Math.Clamp((index < 0 ? 3 : index) + direction, 0, Speeds.Length - 1);
        SetRate(Speeds[index]);
    }

    // --- Pistes audio et sous-titres ---

    private void AudioMenu_Opening(object sender, object e)
    {
        AudioMenu.Items.Clear();
        if (_player is null || _player.AudioTrackDescription.Length == 0)
        {
            AudioMenu.Items.Add(new MenuFlyoutItem { Text = "Aucune piste audio", IsEnabled = false });
            return;
        }
        foreach (var track in _player.AudioTrackDescription)
        {
            var item = new RadioMenuFlyoutItem { Text = TrackName(track), GroupName = "audio", IsChecked = track.Id == _player.AudioTrack };
            var id = track.Id;
            item.Click += (_, _) => _player.SetAudioTrack(id);
            AudioMenu.Items.Add(item);
        }
    }

    private void SubtitleMenu_Opening(object sender, object e)
    {
        SubtitleMenu.Items.Clear();
        if (_player is not null)
        {
            var tracks = _player.SpuDescription;
            if (tracks.Length == 0)
                SubtitleMenu.Items.Add(new MenuFlyoutItem { Text = "Aucun sous-titre", IsEnabled = false });
            foreach (var track in tracks)
            {
                var item = new RadioMenuFlyoutItem { Text = TrackName(track), GroupName = "spu", IsChecked = track.Id == _player.Spu };
                var id = track.Id;
                item.Click += (_, _) => _player.SetSpu(id);
                SubtitleMenu.Items.Add(item);
            }
        }
        SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Ajouter un fichier de sous-titres…", Icon = new FontIcon { Glyph = "" } };
        add.Click += async (_, _) => await AddSubtitleAsync();
        SubtitleMenu.Items.Add(add);
    }

    private static string TrackName(LibVLCSharp.Shared.Structures.TrackDescription track) => track.Id < 0 ? "Désactivé"
        : string.IsNullOrWhiteSpace(track.Name) ? $"Piste {track.Id}" : track.Name.Replace("Track", "Piste");

    private async Task AddSubtitleAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in MediaFiles.SubtitleExtensions)
            picker.FileTypeFilter.Add(ext);
        Attach(picker);
        if (await picker.PickSingleFileAsync() is { } file)
            AddSubtitle(file.Path);
    }

    private void AddSubtitle(string path)
    {
        if (_player is null)
            return;
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        ShowOsd("Sous-titres ajoutés");
    }

    // --- Répétition ---

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _settings.Repeat = _settings.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        _settings.Save();
        UpdateRepeatUi();
        ShowOsd(_settings.Repeat switch
        {
            RepeatMode.All => "Répéter la liste",
            RepeatMode.One => "Répéter ce fichier",
            _ => "Répétition désactivée",
        });
    }

    private void UpdateRepeatUi()
    {
        RepeatIcon.Glyph = _settings.Repeat == RepeatMode.One ? "" : "";
        RepeatIcon.Opacity = _settings.Repeat == RepeatMode.Off ? 0.45 : 1;
        RepeatIcon.Foreground = _settings.Repeat == RepeatMode.Off
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(RepeatButton, _settings.Repeat switch
        {
            RepeatMode.All => "Répéter la liste",
            RepeatMode.One => "Répéter ce fichier",
            _ => "Pas de répétition",
        });
    }

    // --- Menu « Plus » et clic droit ---

    private void MoreMenu_Opening(object sender, object e) => FillMoreMenu(MoreMenu);

    private void FillMoreMenu(MenuFlyout menu)
    {
        menu.Items.Clear();

        var aspect = new MenuFlyoutSubItem { Text = "Format d'image", Icon = new FontIcon { Glyph = "" } };
        foreach (var (label, value) in new[] { ("Automatique", ""), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("1:1", "1:1"), ("Remplir l'écran", "fill") })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "aspect", IsChecked = _aspect == value };
            item.Click += (_, _) => SetAspect(value);
            aspect.Items.Add(item);
        }
        menu.Items.Add(aspect);
        menu.Items.Add(Item("Capture d'écran (S)", "", TakeSnapshot));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Ouvrir des fichiers… (Ctrl+O)", "", () => OpenFiles_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Ouvrir un dossier…", "", () => OpenFolder_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Item("Ouvrir un lien… (Ctrl+L)", "", () => OpenUrl_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new MenuFlyoutSeparator());

        var resume = new ToggleMenuFlyoutItem { Text = "Reprendre là où je m'étais arrêté", IsChecked = _settings.ResumePlayback };
        resume.Click += (_, _) =>
        {
            _settings.ResumePlayback = resume.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(resume);

        var shell = new ToggleMenuFlyoutItem { Text = "« Ouvrir avec NeoMedia » dans l'Explorateur", IsChecked = ShellIntegration.IsEnabled };
        shell.Click += (_, _) =>
        {
            try
            {
                if (shell.IsChecked)
                    ShellIntegration.Enable();
                else
                    ShellIntegration.Disable();
                _settings.ShellIntegration = shell.IsChecked;
                _settings.Save();
            }
            catch (Exception ex)
            {
                ShowError("Intégration à l'Explorateur impossible", ex.Message);
            }
        };
        menu.Items.Add(shell);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        menu.Items.Add(new MenuFlyoutItem { Text = $"NeoMedia {version} · moteur VLC {_libVLC?.Version.Split(' ')[0]}", IsEnabled = false });
    }

    private void ClickLayer_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        if (_current is not null)
        {
            menu.Items.Add(Item(_player?.IsPlaying == true ? "Pause" : "Lecture", _player?.IsPlaying == true ? "" : "", TogglePlayPause));
            menu.Items.Add(Item(_fullscreen ? "Quitter le plein écran" : "Plein écran", _fullscreen ? "" : "", ToggleFullscreen));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        FillMoreMenu(menu);
        menu.ShowAt(ClickLayer, e.GetPosition(ClickLayer));
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private void SetAspect(string value)
    {
        _aspect = value;
        if (_player is null)
            return;
        if (value == "fill")
        {
            // Remplir : on rogne l'image aux proportions de la fenêtre
            _player.AspectRatio = null;
            _player.CropGeometry = $"{(int)Stage.ActualWidth}:{(int)Stage.ActualHeight}";
        }
        else
        {
            _player.CropGeometry = null;
            _player.AspectRatio = value.Length == 0 ? null : value;
        }
    }

    private void TakeSnapshot()
    {
        if (_player is null || _current is null)
            return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NeoMedia");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{_current.Name} {MediaFiles.FormatTime(_player.Time).Replace(':', '-')}.png");
        ShowOsd(_player.TakeSnapshot(0, file, 0, 0) ? "Capture enregistrée dans Images › NeoMedia" : "Capture impossible");
    }

    // ======================================================================
    // Ouvrir
    // ======================================================================

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync();
        if (files.Count > 0)
            LoadPlaylist(files, play: true);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        Attach(picker);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;
        var files = MediaFiles.Expand([folder.Path]);
        if (files.Count == 0)
            ShowError("Aucun fichier à lire", "Ce dossier ne contient ni vidéo ni musique.");
        else
            LoadPlaylist(files, play: true);
    }

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "https://… ou rtsp://…" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Ouvrir un lien",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Adresse d'une vidéo, d'une radio ou d'un flux en direct :", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Lire",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var url = box.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            LoadPlaylist([url], play: true);
        else
            ShowError("Lien invalide", "Saisissez une adresse complète, par exemple https://exemple.com/video.mp4");
    }

    private void RecentLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
            LoadPlaylist([path], play: true);
    }

    private void ShowRecent()
    {
        var recent = _settings.Recent.Where(File.Exists).Take(6).ToList();
        RecentList.ItemsSource = recent;
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowWelcome()
    {
        Title = "NeoMedia";
        TitleMediaText.Text = "";
        AudioPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        ShowRecent();
    }

    private async Task<List<string>> PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        Attach(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).OrderBy(p => p, NaturalComparer.Instance).ToList();
    }

    private void Attach(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    // --- Glisser-déposer ---

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.WebLink))
            return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = PlaylistPanel.Visibility == Visibility.Visible ? "Ajouter à la liste" : "Lire";
    }

    /// <summary>Vidéos, musiques ou dossiers : lus (ou ajoutés à la liste si elle est ouverte). Sous-titres : chargés.</summary>
    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            paths.AddRange((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
        else if (e.DataView.Contains(StandardDataFormats.WebLink))
            paths.Add((await e.DataView.GetWebLinkAsync()).AbsoluteUri);

        var subtitles = paths.Where(MediaFiles.IsSubtitle).ToList();
        if (subtitles.Count > 0 && _current is not null)
            subtitles.ForEach(AddSubtitle);

        var media = MediaFiles.Expand(paths.Where(p => !MediaFiles.IsSubtitle(p)));
        if (media.Count == 0)
            return;
        var append = PlaylistPanel.Visibility == Visibility.Visible && _playlist.Count > 0;
        LoadPlaylist(media, play: !append || _current is null, append: append);
    }

    // ======================================================================
    // Plein écran, commandes qui se masquent, affichage furtif
    // ======================================================================

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ClickLayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

    private void ClickLayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_current is not null)
            TogglePlayPause();
    }

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        TitleRow.Height = new GridLength(fullscreen ? 0 : 40);
        AppTitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        FullscreenIcon.Glyph = fullscreen ? "" : "";
        ShowControls();
    }

    private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();

    private void ControlsPanel_PointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOnControls = true;

    private void ControlsPanel_PointerExited(object sender, PointerRoutedEventArgs e) => _pointerOnControls = false;

    private void ShowControls(bool autoHide = true)
    {
        ControlsPanel.Visibility = Visibility.Visible;
        if (autoHide && _player?.IsPlaying == true)
            RestartHideTimer();
        else
            _hideTimer.Stop();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Pendant la lecture, les commandes disparaissent après quelques secondes sans bouger la souris.</summary>
    private void TryHideControls()
    {
        var menuOpen = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Count > 0;
        if (_player?.IsPlaying != true || _pointerOnControls || menuOpen || PlaylistPanel.Visibility == Visibility.Visible)
        {
            if (_player?.IsPlaying == true)
                RestartHideTimer();
            return;
        }
        ControlsPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowOsd(string text)
    {
        OsdText.Text = text;
        Osd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void Stage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (PlaylistPanel.Visibility == Visibility.Visible && e.OriginalSource is DependencyObject source && IsInside(source, PlaylistPanel))
            return; // la molette fait défiler la liste
        var delta = e.GetCurrentPoint(Stage).Properties.MouseWheelDelta;
        ChangeVolume(delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (n == container)
                return true;
        }
        return false;
    }

    // ======================================================================
    // Clavier (comme VLC)
    // ======================================================================

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Saisie dans une zone de texte (lien, etc.) : on n'intercepte rien
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or PasswordBox)
            return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space:
                TogglePlayPause();
                break;
            case VirtualKey.Right:
                Seek(ctrl ? 60_000 : shift ? 3_000 : 10_000);
                break;
            case VirtualKey.Left:
                Seek(ctrl ? -60_000 : shift ? -3_000 : -10_000);
                break;
            case VirtualKey.Up:
                ChangeVolume(5);
                break;
            case VirtualKey.Down:
                ChangeVolume(-5);
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                ToggleFullscreen();
                break;
            case VirtualKey.Escape when _fullscreen:
                SetFullscreen(false);
                break;
            case VirtualKey.M:
                SetMute(!_settings.Muted);
                break;
            case VirtualKey.N:
                PlayNext(userAction: true);
                break;
            case VirtualKey.P:
                PlayPrevious();
                break;
            case VirtualKey.S when !ctrl:
                TakeSnapshot();
                break;
            case VirtualKey.L when ctrl:
                OpenUrl_Click(this, new RoutedEventArgs());
                break;
            case VirtualKey.L:
                TogglePlaylist();
                break;
            case VirtualKey.O when ctrl:
                OpenFiles_Click(this, new RoutedEventArgs());
                break;
            case (VirtualKey)219: // [
                StepRate(-1);
                break;
            case (VirtualKey)221: // ]
                StepRate(1);
                break;
            case VirtualKey.Home when _player is { IsSeekable: true }:
                _player.Time = 0;
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
        {
            e.Handled = true;
            ShowControls();
        }
        await Task.CompletedTask;
    }

    // ======================================================================
    // Fermeture
    // ======================================================================

    private void SaveCurrentPosition()
    {
        if (_player is null || _current is not { IsFile: true } item)
            return;
        _settings.SavePosition(item.Source, _player.Time, _player.Length);
        _settings.Save();
    }

    /// <summary>Position mémorisée, puis arrêt du moteur hors du thread de l'interface (Stop peut bloquer).</summary>
    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveCurrentPosition();
        _settings.Save();
        var player = _player;
        var media = _media;
        var libVLC = _libVLC;
        VideoView.MediaPlayer = null;
        _ = Task.Run(() =>
        {
            player?.Stop();
            player?.Dispose();
            media?.Dispose();
            libVLC?.Dispose();
        });
    }
}
