using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neo.Shared;
using NeoSuite.Models;
using NeoSuite.Services;
using Windows.Graphics;
using WinRT;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using VirtualKey = Windows.System.VirtualKey;

namespace NeoSuite;

/// <summary>
/// Le panneau NeoSuite : une barre vitrée ancrée à droite de l'écran, qui glisse depuis le bord
/// et liste les applications Neo. NeoSuite reste ensuite dans la barre système : son icône ou le
/// raccourci global (Ctrl + Alt + N par défaut) rouvrent le panneau.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int PanelWidth = 380;   // pixels logiques
    private const int ScreenMargin = 12;  // écart avec les bords de l'écran

    private readonly IntPtr _hwnd;
    private readonly DesktopAcrylicController _acrylic;
    private readonly SystemBackdropConfiguration _backdropConfig;
    private readonly DispatcherQueueTimer _clock;
    private readonly TrayIcon _tray;
    private readonly GlobalHotKey _hotKey = new();
    private TrayMenuWindow? _trayMenu;

    private List<NeoApp> _apps = [];
    private bool _isOpen;
    private bool _animating;
    private bool _quitting;
    private bool _capturingHotKey;
    private DateTime _shownAt;

    // Fermeture automatique : le panneau se range après quelques secondes sans la souris dessus
    private readonly DispatcherQueueTimer _autoClose;
    private DispatcherQueueTimer? _slideTimer;
    private bool _pointerInside;
    private bool _menuOrDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Fenêtre de type « menu » : ni cadre, ni barre de titre, ni liseré blanc autour
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(IconPath);

        var round = 2; // DWMWCP_ROUND : coins arrondis Windows 11
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        var noTransitions = 1; // pas d'animation système : on fait notre propre glissement
        DwmSetWindowAttribute(_hwnd, 3, ref noTransitions, sizeof(int));

        // Verre acrylique teinté bleu nuit, qui reste translucide même sans le focus
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _acrylic = new DesktopAcrylicController
        {
            TintColor = Windows.UI.Color.FromArgb(255, 10, 16, 28),
            TintOpacity = 0.45f,
            LuminosityOpacity = 0.8f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 14, 20, 32),
        };
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);

        Activated += MainWindow_Activated;
        AppWindow.Closing += (_, e) =>
        {
            // Alt + F4 : on range le panneau au lieu de quitter
            if (_quitting)
                return;
            e.Cancel = true;
            HidePanel();
        };

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateClock();

        _autoClose = DispatcherQueue.CreateTimer();
        _autoClose.IsRepeating = false;
        _autoClose.Tick += (_, _) =>
        {
            if (CanAutoClose())
                HidePanel();
            else
                RestartAutoClose();
        };
        var seconds = AppSettings.Current.AutoCloseSeconds.ToString();
        AutoCloseCombo.SelectedItem = AutoCloseCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == seconds)
            ?? AutoCloseCombo.Items[2];

        _tray = new TrayIcon(IconPath, "NeoSuite");
        _tray.OpenRequested += (_, _) => Toggle();
        _tray.MenuRequested += (_, e) => (_trayMenu ??= new TrayMenuWindow(this)).ShowAt(e.X, e.Y);
        _tray.Show();

        _hotKey.Pressed += (_, _) => Toggle();
        RegisterHotKey(AppSettings.Current.GetHotKey(), save: false);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Neo Suite {version?.ToString(3)}";
        StartupToggle.IsOn = StartupService.IsEnabled;
        StartupService.RefreshPath();

        // Même au démarrage de Windows, panneau fermé : les recherches hebdomadaires des applis partent
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_apps.Count == 0)
                _apps = AppCatalog.Load();
            RunBackgroundChecks();
        });
    }

    /// <summary>Applis qui n'ont pas relevé leur état depuis une semaine (NeoDriver : pilotes à mettre à jour) : recherche silencieuse.</summary>
    private void RunBackgroundChecks() =>
        AppCatalog.StartBackgroundChecks(_apps, app => DispatcherQueue.TryEnqueue(() => AppCatalog.Refresh(app)));

    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    public HotKeyGesture? CurrentHotKey => _hotKey.Current;

    // ---------- Ouverture / fermeture du panneau ----------

    public void Toggle()
    {
        if (_isOpen)
            HidePanel();
        else
            OpenPanel();
    }

    /// <summary>Affiche le panneau en le faisant glisser depuis le bord droit.</summary>
    public void OpenPanel(bool showSettings = false)
    {
        ShowView(settings: showSettings);
        if (_isOpen || _animating)
        {
            Activate();
            return;
        }

        _isOpen = true;
        GreetingText.Text = Greeting();
        SearchBox.Text = "";
        LoadApps();
        UpdateClock();
        _clock.Start();

        var (finalRect, offscreenX) = ComputePlacement();
        AppWindow.MoveAndResize(new RectInt32(offscreenX, finalRect.Y, finalRect.Width, finalRect.Height));
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
        _shownAt = DateTime.Now;

        // Pas de liseré blanc (DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE), appliqué une fois la fenêtre affichée
        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));

        if (!showSettings)
            SearchBox.Focus(FocusState.Programmatic);
        _pointerInside = false;
        Slide(offscreenX, finalRect.X, onDone: RestartAutoClose);
    }

    /// <summary>Range le panneau (glissement vers la droite) ; NeoSuite reste dans la barre système.</summary>
    public void HidePanel()
    {
        if (!_isOpen || _animating)
            return;
        _isOpen = false;
        CancelHotKeyCapture();
        _clock.Stop();
        _autoClose.Stop();
        var (_, offscreenX) = ComputePlacement();
        Slide(AppWindow.Position.X, offscreenX, onDone: () => AppWindow.Hide());
    }

    /// <summary>Ferme réellement NeoSuite (et retire l'icône de la barre système).</summary>
    public void Quit()
    {
        _quitting = true;
        _hotKey.Dispose();
        _tray.Dispose();
        Application.Current.Exit();
    }

    /// <summary>Premier lancement en arrière-plan : indique comment ouvrir le panneau.</summary>
    public void ShowWelcomeBalloon()
    {
        var hotKey = _hotKey.Current?.ToString() ?? "l'icône NeoSuite";
        _tray.ShowBalloon("NeoSuite est prêt", $"Appuyez sur {hotKey} ou cliquez sur l'icône pour ouvrir vos applications Neo.");
    }

    private (RectInt32 Final, int OffscreenX) ComputePlacement()
    {
        var work = DisplayArea.Primary.WorkArea;
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)(PanelWidth * scale), margin = (int)(ScreenMargin * scale);
        var final = new RectInt32(work.X + work.Width - width - margin, work.Y + margin, width, work.Height - 2 * margin);
        return (final, work.X + work.Width + margin);
    }

    /// <summary>Déplace la fenêtre de <paramref name="fromX"/> à <paramref name="toX"/> avec un ralenti (≈ 220 ms).</summary>
    private void Slide(int fromX, int toX, Action? onDone)
    {
        const int durationMs = 220;
        _animating = true;
        var start = DateTime.Now;
        var y = AppWindow.Position.Y;
        // Gardé dans un champ : un minuteur sans référence peut être supprimé par le ramasse-miettes en pleine
        // animation, le panneau restait alors coincé au bord de l'écran et ne s'ouvrait plus
        _slideTimer?.Stop();
        var timer = _slideTimer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (t, _) =>
        {
            var progress = Math.Min(1.0, (DateTime.Now - start).TotalMilliseconds / durationMs);
            var eased = 1 - Math.Pow(1 - progress, 3); // ease-out cubique
            AppWindow.Move(new PointInt32((int)(fromX + (toX - fromX) * eased), y));
            if (progress >= 1)
            {
                t.Stop();
                _animating = false;
                onDone?.Invoke();
            }
        };
        timer.Start();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Clic en dehors du panneau (ou appli lancée) : on le range, comme un menu.
        // Sauf pendant une installation : la fenêtre UAC fait perdre le focus, il ne faut pas fermer
        if (args.WindowActivationState != WindowActivationState.Deactivated
            || DateTime.Now - _shownAt < TimeSpan.FromMilliseconds(400)
            || _apps.Any(a => a.IsBusy) || _menuOrDialogOpen)
            return;

        // Le focus passe à une autre fenêtre de NeoSuite (menu, boîte de dialogue) : on reste ouvert
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != Environment.ProcessId && !_menuOrDialogOpen)
                HidePanel();
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    private bool CanAutoClose() =>
        _isOpen && !_pointerInside && !_menuOrDialogOpen && !_capturingHotKey && !_apps.Any(a => a.IsBusy);

    private void RestartAutoClose()
    {
        _autoClose.Stop();
        var seconds = AppSettings.Current.AutoCloseSeconds;
        if (!_isOpen || seconds <= 0)
            return;
        _autoClose.Interval = TimeSpan.FromSeconds(seconds);
        _autoClose.Start();
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        _autoClose.Stop();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        RestartAutoClose();
    }

    private void AutoCloseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AutoCloseCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var seconds))
            return;
        if (AppSettings.Current.AutoCloseSeconds == seconds)
            return;
        AppSettings.Current.AutoCloseSeconds = seconds;
        AppSettings.Current.Save();
        RestartAutoClose();
    }

    // ---------- Bibliothèque ----------

    /// <summary>
    /// Charge le catalogue une seule fois, puis rafraîchit l'état à chaque ouverture : recharger la liste
    /// ferait perdre la progression d'une installation en cours.
    /// </summary>
    private void LoadApps()
    {
        if (_apps.Count == 0)
            _apps = AppCatalog.Load();
        else
            foreach (var app in _apps.Where(a => !a.IsBusy))
                AppCatalog.Refresh(app);
        ApplyFilter();
        _ = RefreshLiveStatusAsync();
        _ = RefreshUpdateAsync();
        _ = AppCatalog.MergeOnlineCatalogAsync(_apps); // paquets plus récents publiés sur GitHub
        RunBackgroundChecks();
    }

    /// <summary>Demande aux applis lancées leur état (« 2 mises à jour »…) pour la pastille de leur tuile.</summary>
    private async Task RefreshLiveStatusAsync()
    {
        foreach (var app in _apps)
        {
            if (!app.IsRunning)
            {
                app.LiveStatus = null;
                app.LiveActions = null;
                continue;
            }
            var info = await NeoIpc.DescribeAsync(app.Id);
            app.LiveStatus = info?.Status;
            app.LiveActions = info?.Actions;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? _apps
            : _apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || a.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || a.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        AppsRepeater.ItemsSource = visible;
        NoResultText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ComingSoonCard.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _apps.Count == 1 ? "1 appli" : $"{_apps.Count} applis";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NeoApp app || app.IsBusy)
            return;
        if (app.HasUpdate || !app.IsInstalled)
            await InstallAppAsync(app);
        else
            LaunchApp(app);
    }

    /// <summary>
    /// Clic droit sur une tuile : les actions de l'appli si elle tourne (son menu de barre système),
    /// puis Ouvrir, Installer, Mettre à jour, Désinstaller selon l'état.
    /// </summary>
    private void AppTile_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NeoApp app } tile || app.IsBusy)
            return;
        e.Handled = true;

        var menu = BuildTileMenu(app);
        if (menu.Items.Count == 0)
            return;

        // Menu dessiné dans le panneau : sinon WinUI l'ouvre dans une fenêtre à part, le panneau
        // perd le focus, se range… et referme le menu aussitôt
        menu.ShouldConstrainToRootBounds = true;
        menu.Closed += (_, _) =>
        {
            _menuOrDialogOpen = false;
            RestartAutoClose();
        };
        _menuOrDialogOpen = true;
        if (e.TryGetPosition(tile, out var point))
            menu.ShowAt(tile, point);
        else
            menu.ShowAt(tile); // Maj + F10 / touche Menu
    }

    private MenuFlyout BuildTileMenu(NeoApp app)
    {
        var menu = new MenuFlyout();
        var actions = app.IsRunning ? app.LiveActions : null;
        if (actions is not null)
        {
            foreach (var action in actions)
                menu.Items.Add(MenuItem(action.Text, action.Glyph, () => _ = RunAppActionAsync(app, action)));
        }
        else if (app.IsInstalled)
        {
            menu.Items.Add(MenuItem("Ouvrir", "", () => LaunchApp(app)));
        }

        // Gestion de l'installation, séparée des actions de l'appli
        var manage = new List<MenuFlyoutItem>();
        if (app.HasUpdate)
            manage.Add(MenuItem(app.AvailableVersion > app.InstalledVersion ? $"Mettre à jour vers {app.AvailableVersion}" : "Mettre à jour (nouvelle compilation)", "", () => _ = InstallAppAsync(app)));
        if (!app.IsInstalled && app.CanInstall)
            manage.Add(MenuItem(actions is null ? "Installer" : "Installer la version de la suite", "", () => _ = InstallAppAsync(app)));
        if (app.IsInstalled)
            manage.Add(MenuItem("Désinstaller", "", () => _ = UninstallAppAsync(app)));

        if (manage.Count > 0 && menu.Items.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var item in manage)
            menu.Items.Add(item);
        return menu;
    }

    /// <summary>Exécute une action proposée par l'appli (ex. « Rechercher des mises à jour » de NeoDriver).</summary>
    private async Task RunAppActionAsync(NeoApp app, NeoAction action)
    {
        var showsWindow = action.Command is "open" || action.Command.StartsWith("page:");
        if (showsWindow)
        {
            // Autorise l'appli à passer au premier plan (NeoSuite vient de recevoir le clic)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(app.Id))
                AllowSetForegroundWindow(p.Id);
            HidePanel();
        }
        else if (action.Command != "quit")
        {
            app.LiveStatus = "En cours…";
        }

        var result = await NeoIpc.RunAsync(app.Id, action.Command);
        if (result is { Title: not null })
            _tray.ShowBalloon(result.Title, result.Message ?? "");

        await Task.Delay(action.Command == "quit" ? 1500 : 0);
        AppCatalog.Refresh(app);
        await RefreshLiveStatusAsync();
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private void LaunchApp(NeoApp app)
    {
        AppCatalog.Launch(app);
        HidePanel();
    }

    private async Task InstallAppAsync(NeoApp app)
    {
        var update = app.IsInstalled;
        try
        {
            await AppInstaller.InstallAsync(app);
            NotifyDone($"{app.Name} est installé", update ? $"{app.Name} a été mis à jour." : $"{app.Name} est prêt : cliquez sur sa tuile pour l'ouvrir.");
        }
        catch (OperationCanceledException ex)
        {
            NotifyDone("Installation annulée", ex.Message);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Impossible d'installer {app.Name}", ex.Message);
        }
        finally
        {
            RestartAutoClose();
        }
    }

    private async Task UninstallAppAsync(NeoApp app)
    {
        var removeData = new CheckBox { Content = "Supprimer aussi ses paramètres et son historique" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Désinstaller {app.Name} ?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{app.Name} sera supprimé de ce PC. Vous pourrez le réinstaller à tout moment depuis NeoSuite.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    removeData,
                },
            },
            PrimaryButtonText = "Désinstaller",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };

        _menuOrDialogOpen = true;
        var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        _menuOrDialogOpen = false;
        if (!confirmed)
        {
            RestartAutoClose();
            return;
        }

        try
        {
            await AppInstaller.UninstallAsync(app, removeData.IsChecked == true);
            NotifyDone($"{app.Name} est désinstallé", $"{app.Name} a été supprimé de ce PC.");
        }
        catch (OperationCanceledException ex)
        {
            NotifyDone("Désinstallation annulée", ex.Message);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Impossible de désinstaller {app.Name}", ex.Message);
        }
        finally
        {
            RestartAutoClose();
        }
    }

    /// <summary>Fin d'opération : si le panneau a été rangé entre-temps, on prévient par une notification.</summary>
    private void NotifyDone(string title, string message)
    {
        if (!_isOpen)
            _tray.ShowBalloon(title, message);
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (!_isOpen)
        {
            _tray.ShowBalloon(title, message);
            return;
        }
        _menuOrDialogOpen = true;
        await new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.ShowAsync();
        _menuOrDialogOpen = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePanel();

    // ---------- Mise à jour de NeoSuite ----------

    /// <summary>Cherche (au plus toutes les deux minutes, sauf demande explicite) puis affiche ou masque l'avis.</summary>
    private async Task RefreshUpdateAsync(bool force = false)
    {
        var recent = UpdateService.LastCheck is { } last && DateTime.Now - last < TimeSpan.FromMinutes(2);
        var update = force || !recent ? await UpdateService.CheckAsync(force) : UpdateService.Available;
        ShowUpdate(update);
    }

    private void ShowUpdate(SuiteUpdate? update)
    {
        UpdateBanner.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        if (update is null)
            return;
        UpdateTitle.Text = $"NeoSuite {update.Version.ToString(3)} est disponible";
        UpdateDetail.Text = update.IsOnline
            ? $"Vous avez la {UpdateService.CurrentVersion.ToString(3)}. Téléchargement depuis GitHub ({FormatSize(update.DownloadSize)})."
            : $"Vous avez la {UpdateService.CurrentVersion.ToString(3)}. Installateur trouvé dans {update.FolderLabel}.";
        ToolTipService.SetToolTip(UpdateBanner, update.Notes.Count == 0 ? null
            : "Nouveautés :\n" + string.Join("\n", update.Notes.Take(6).Select(n => "• " + n)));
    }

    private bool _downloadingUpdate;

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } update || _downloadingUpdate)
            return;
        try
        {
            if (update.IsOnline)
            {
                // Téléchargement depuis GitHub : le panneau reste ouvert pour suivre la progression
                _downloadingUpdate = true;
                _menuOrDialogOpen = true;
                var progress = new Progress<double>(p => UpdateDetail.Text = $"Téléchargement de NeoSuite {update.Version.ToString(3)}… {p:P0}");
                update = await UpdateService.DownloadAsync(update, progress);
                UpdateDetail.Text = "Téléchargement terminé : ouverture de l'installateur…";
            }

            // L'installateur demande la confirmation Windows, ferme NeoSuite, le met à jour puis le rouvre
            UpdateService.Launch(update);
            HidePanel();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Confirmation refusée : rien ne change (le téléchargement est gardé pour la prochaine fois)
            ShowUpdate(UpdateService.Available);
        }
        catch (Exception ex)
        {
            ShowUpdate(UpdateService.Available);
            UpdateStatusText.Text = update.IsOnline
                ? $"Le téléchargement a échoué : {ex.Message}. Vérifiez la connexion puis réessayez."
                : $"Impossible de lancer l'installateur : {ex.Message}";
            UpdateStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _downloadingUpdate = false;
            _menuOrDialogOpen = false;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} Go" : $"{Math.Max(1, bytes / (1 << 20))} Mo";

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Recherche en cours…";
        UpdateStatusText.Visibility = Visibility.Visible;
        await RefreshUpdateAsync(force: true);
        UpdateStatusText.Text = UpdateService.Available is { } update
            ? $"NeoSuite {update.Version.ToString(3)} est disponible ({(update.IsOnline ? "sur GitHub" : $"trouvée dans {update.FolderLabel}")}). Elle s'affiche en haut du panneau."
            : "NeoSuite est à jour : aucune version plus récente sur GitHub, ni d'installateur plus récent sur cet ordinateur.";
        CheckUpdateButton.IsEnabled = true;
    }

    /// <summary>Saisie du raccourci : interceptée avant les contrôles (sinon Entrée/Espace cliqueraient le bouton).</summary>
    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        RestartAutoClose(); // on tape au clavier : le panneau est utilisé
        if (_capturingHotKey)
            CaptureHotKey(e);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return;

        if (e.Key == VirtualKey.Escape)
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
                ShowView(settings: false);
            else
                HidePanel();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && AppsRepeater.ItemsSource is List<NeoApp> { Count: > 0 } visible && visible[0].IsInstalled)
        {
            // Entrée dans la recherche : lance la première appli trouvée
            LaunchApp(visible[0]);
        }
    }

    // ---------- Paramètres ----------

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowView(settings: SettingsPanel.Visibility != Visibility.Visible);

    private void ShowView(bool settings)
    {
        if (!settings)
            CancelHotKeyCapture();
        var library = settings ? Visibility.Collapsed : Visibility.Visible;
        ClockPanel.Visibility = library;
        SearchBox.Visibility = library;
        LibraryPanel.Visibility = library;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        SettingsIcon.Glyph = settings ? "" : ""; // flèche retour / engrenage
        ToolTipService.SetToolTip(SettingsButton, settings ? "Retour aux applications" : "Paramètres");
        UpdateHotKeyDisplay();
        if (settings)
        {
            UpdateVersionText.Text = $"NeoSuite {UpdateService.CurrentVersion.ToString(3)} · mises à jour depuis GitHub (et les installateurs déjà présents sur cet ordinateur).";
            _ = BuildAdminAppsAsync();
        }
    }

    /// <summary>Un interrupteur « démarrer en administrateur » par appli installée.</summary>
    private async Task BuildAdminAppsAsync()
    {
        AdminAppsPanel.Children.Clear();
        AdminStatusText.Visibility = Visibility.Collapsed;

        // Ouverture directe sur les paramètres : la bibliothèque n'est pas encore chargée
        if (_apps.Count == 0)
            _apps = AppCatalog.Load();
        var installed = _apps.Where(a => a.IsInstalled).ToList();
        if (installed.Count == 0)
        {
            AdminAppsPanel.Children.Add(new TextBlock
            {
                Text = "Installez une appli pour régler ses droits.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["GlassTextTertiaryBrush"],
            });
            return;
        }

        var toggles = new Dictionary<NeoApp, ToggleSwitch>();
        foreach (var app in installed)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(app.IconUri)),
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock { Text = app.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            var toggle = new ToggleSwitch
            {
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
                IsEnabled = false, // le temps de lire l'état actuel
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"Démarrer {app.Name} en administrateur");
            Grid.SetColumn(toggle, 2);

            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(toggle);
            AdminAppsPanel.Children.Add(row);
            toggles[app] = toggle;
        }

        await AppCatalog.RefreshAdminStateAsync(installed);
        foreach (var (app, toggle) in toggles)
        {
            toggle.IsOn = app.RunsAsAdmin;
            toggle.IsEnabled = true;
            toggle.Toggled += (_, _) => _ = SetRunAsAdminAsync(app, toggle);
        }
    }

    private async Task SetRunAsAdminAsync(NeoApp app, ToggleSwitch toggle)
    {
        var enable = toggle.IsOn;
        if (enable == app.RunsAsAdmin)
            return;

        // La fenêtre UAC prend le focus : le panneau doit rester ouvert
        toggle.IsEnabled = false;
        _menuOrDialogOpen = true;
        string message;
        var error = false;
        try
        {
            await AppInstaller.SetRunAsAdminAsync(app, enable);
            app.RunsAsAdmin = enable;
            message = !enable ? $"{app.Name} redemandera les droits administrateur quand il en aura besoin."
                : app.IsRunning ? $"{app.Name} démarrera en administrateur à son prochain lancement."
                : $"{app.Name} démarrera désormais en administrateur.";
        }
        catch (Exception ex)
        {
            error = true;
            message = ex is OperationCanceledException ? "Opération annulée : Windows n'a pas donné son accord." : ex.Message;
            toggle.IsOn = app.RunsAsAdmin; // le gestionnaire ignore ce retour en arrière (état inchangé)
        }
        finally
        {
            toggle.IsEnabled = true;
            _menuOrDialogOpen = false;
            RestartAutoClose();
        }

        AdminStatusText.Text = message;
        AdminStatusText.Foreground = error
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 153, 164))
            : (Brush)Application.Current.Resources["GlassTextSecondaryBrush"];
        AdminStatusText.Visibility = Visibility.Visible;
    }

    private void UpdateHotKeyDisplay()
    {
        var current = _hotKey.Current;
        HotKeyText.Text = _capturingHotKey ? "Appuyez sur la combinaison…" : current?.ToString() ?? "Aucun";
        ChangeHotKeyButton.Content = _capturingHotKey ? "Annuler" : "Modifier";
        HotKeyBox.BorderBrush = _capturingHotKey
            ? (Brush)Application.Current.Resources["NeoAccentBrush"]
            : (Brush)Application.Current.Resources["GlassStrokeBrush"];
        ResetHotKeyButton.Visibility = current == HotKeyGesture.Default && !_capturingHotKey ? Visibility.Collapsed : Visibility.Visible;
        FooterHintText.Text = current is null ? "Échap pour fermer" : $"{current} pour ouvrir";
        _tray.UpdateTooltip(current is null ? "NeoSuite" : $"NeoSuite ({current})");
    }

    private void ChangeHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotKey)
        {
            CancelHotKeyCapture();
            return;
        }
        // Le raccourci actuel est libéré pendant la saisie, pour pouvoir le retaper
        _capturingHotKey = true;
        _hotKey.Unregister();
        SetHotKeyStatus(null);
        UpdateHotKeyDisplay();
    }

    private void CancelHotKeyCapture()
    {
        if (!_capturingHotKey)
            return;
        _capturingHotKey = false;
        RegisterHotKey(AppSettings.Current.GetHotKey(), save: false);
    }

    private void CaptureHotKey(KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var vk = (int)e.Key;
        if (HotKeyGesture.IsModifierKey(vk))
            return; // on attend la touche principale

        var modifiers = HotKeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= HotKeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotKeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotKeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotKeyModifiers.Windows;

        if (e.Key == VirtualKey.Escape && modifiers == HotKeyModifiers.None)
        {
            CancelHotKeyCapture();
            UpdateHotKeyDisplay();
            return;
        }

        var gesture = new HotKeyGesture(modifiers, vk);
        if (!gesture.IsValid)
        {
            SetHotKeyStatus("Ajoutez au moins Ctrl, Alt, Maj ou Win à la touche.", error: true);
            return;
        }

        _capturingHotKey = false;
        if (!RegisterHotKey(gesture, save: true))
            RegisterHotKey(AppSettings.Current.GetHotKey(), save: false);
    }

    private void ResetHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotKey = false;
        if (!RegisterHotKey(HotKeyGesture.Default, save: true))
            RegisterHotKey(AppSettings.Current.GetHotKey(), save: false);
    }

    /// <summary>Enregistre le raccourci global ; s'il est déjà pris par une autre appli, le signale.</summary>
    private bool RegisterHotKey(HotKeyGesture gesture, bool save)
    {
        var ok = _hotKey.Register(gesture);
        if (ok && save)
        {
            AppSettings.Current.HotKey = gesture.Serialize();
            AppSettings.Current.Save();
            SetHotKeyStatus($"Raccourci enregistré : {gesture}", error: false);
        }
        else if (!ok)
        {
            SetHotKeyStatus($"{gesture} est déjà utilisé par une autre application. Choisissez-en un autre.", error: true);
        }
        UpdateHotKeyDisplay();
        return ok;
    }

    private void SetHotKeyStatus(string? message, bool error = false)
    {
        HotKeyStatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        HotKeyStatusText.Text = message ?? "";
        HotKeyStatusText.Foreground = error
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 153, 164))
            : (Brush)Application.Current.Resources["GlassTextSecondaryBrush"];
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (StartupToggle.IsOn != StartupService.IsEnabled)
            StartupService.SetEnabled(StartupToggle.IsOn);
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e) => Quit();

    // ---------- Divers ----------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        ClockText.Text = now.ToString("HH:mm", fr);
        var date = now.ToString("dddd d MMMM", fr);
        DateText.Text = char.ToUpper(date[0], fr) + date[1..];
    }

    private static string Greeting()
    {
        var hour = DateTime.Now.Hour;
        var hello = hour is >= 18 or < 5 ? "Bonsoir" : "Bonjour";
        var name = Environment.UserName;
        return string.IsNullOrEmpty(name) ? hello : $"{hello} {char.ToUpper(name[0])}{name[1..]}";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
