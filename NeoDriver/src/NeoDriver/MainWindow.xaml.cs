using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.Shared;
using NeoDriver.Services;
using NeoDriver.Views;
using Windows.Graphics;

namespace NeoDriver;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Updates"] = typeof(UpdatesPage),
        ["Install"] = typeof(InstallPage),
        ["Vendors"] = typeof(VendorsPage),
        ["Tools"] = typeof(ToolsPage),
        ["Hardware"] = typeof(HardwarePage),
        ["History"] = typeof(HistoryPage),
        ["Settings"] = typeof(SettingsPage),
    };

    public MainWindow(string? startPage = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1240, 820));
        // En dessous, les onglets de la page Matériel ne tiennent plus sur une ligne
        _minSize = new WindowMinSize(this, 1060, 660);

        if (AdminService.IsAdministrator)
        {
            Title = "NeoDriver (administrateur)";
            AdminTag.Visibility = Visibility.Visible;
        }

        SetTheme(AppSettings.Current.Theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });

        // L'élément Paramètres n'existe qu'une fois le NavigationView chargé
        NavView.Loaded += (_, _) => NavigateTo(startPage is not null && Pages.ContainsKey(startPage) ? startPage : "Dashboard");

        if (AppSettings.Current.ScanOnStartup && startPage is null)
            _ = ScanOnStartupAsync();

        // Pas d'icône dans la barre système : le menu de NeoDriver est dans NeoSuite
        NeoIpc.StartServer("NeoDriver", () => OnUiThread(DescribeForSuite), command => OnUiThread(() => RunSuiteCommandAsync(command)));
    }

    private readonly WindowMinSize _minSize;

    // ---------- Intégration NeoSuite ----------

    /// <summary>État et actions de NeoDriver, affichés par NeoSuite (pastille et clic droit sur la tuile).</summary>
    private Task<NeoAppInfo> DescribeForSuite()
    {
        var count = DriverUpdateService.LastResult?.Count;
        var status = DriverUpdateService.IsSearching ? "Recherche…"
            : count switch
            {
                null => null,
                0 => "À jour",
                1 => "1 mise à jour",
                var n => $"{n} mises à jour",
            };

        NeoAction[] actions =
        [
            new("open", "Ouvrir NeoDriver", ""),
            new("scan", "Rechercher des mises à jour", ""),
            new("page:Updates", "Voir les mises à jour", ""),
            new("page:Hardware", "Matériel", ""),
            new("page:Settings", "Paramètres", ""),
            new("quit", "Quitter NeoDriver", ""),
        ];
        return Task.FromResult(new NeoAppInfo(status, actions));
    }

    private async Task<NeoActionResult?> RunSuiteCommandAsync(string command)
    {
        switch (command)
        {
            case "open":
                ShowWindow();
                return null;
            case "scan":
                var (title, message) = await ScanAsync();
                return new NeoActionResult(title, message);
            case "quit":
                // Laisse le temps à la réponse de partir avant de fermer
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Quit);
                return null;
            default:
                if (command.StartsWith("page:"))
                    ShowPage(command["page:".Length..]);
                return null;
        }
    }

    /// <summary>Exécute <paramref name="work"/> sur le thread UI depuis le thread du tube nommé.</summary>
    private Task<T> OnUiThread<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public ElementTheme CurrentTheme => RootGrid.RequestedTheme;

    /// <summary>Réaffiche la fenêtre sur une page donnée (depuis NeoSuite).</summary>
    public void ShowPage(string tag)
    {
        ShowWindow();
        NavigateTo(tag);
    }

    /// <summary>Ferme l'application.</summary>
    public void Quit() => Application.Current.Exit();

    public void ShowWindow()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>Recherche les mises à jour et renvoie le texte de la notification à afficher.</summary>
    private async Task<(string Title, string Message)> ScanAsync()
    {
        try
        {
            var updates = await DriverUpdateService.SearchAsync();
            SetUpdatesBadge(updates.Count);
            return (
                updates.Count == 0 ? "Votre PC est à jour" : updates.Count == 1 ? "1 mise à jour disponible" : $"{updates.Count} mises à jour disponibles",
                updates.Count == 0 ? "Aucune mise à jour de pilote proposée par Windows Update." : "Ouvrez NeoDriver pour les installer.");
        }
        catch (Exception ex)
        {
            return ("Recherche impossible", ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private async Task ScanOnStartupAsync()
    {
        try
        {
            var updates = await DriverUpdateService.SearchAsync();
            SetUpdatesBadge(updates.Count);
        }
        catch (Exception)
        {
            // Analyse silencieuse : l'erreur s'affichera si l'utilisateur ouvre la page Mises à jour
        }
    }

    public void SetTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
    }

    public void SetUpdatesBadge(int count)
    {
        UpdatesBadge.Value = count;
        UpdatesBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void NavigateTo(string tag)
    {
        if (tag == "Settings")
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string)item.Tag == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null || !Pages.TryGetValue(tag, out var page) || ContentFrame.CurrentSourcePageType == page)
            return;

        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
