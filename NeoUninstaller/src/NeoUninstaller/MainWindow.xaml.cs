using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.Shared;
using NeoUninstaller.Models;
using NeoUninstaller.Services;
using NeoUninstaller.Views;
using Windows.Graphics;

namespace NeoUninstaller;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Programs"] = typeof(ProgramsPage),
        ["WindowsApps"] = typeof(WindowsAppsPage),
        ["Startup"] = typeof(StartupPage),
        ["Monitor"] = typeof(MonitorPage),
        ["Drivers"] = typeof(DriversPage),
        ["Cleaner"] = typeof(CleanerPage),
        ["History"] = typeof(HistoryPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private readonly WindowMinSize _minSize;
    private readonly StartupWatcher _watcher = new();

    public MainWindow(string? startPage = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1240, 820));
        _minSize = new WindowMinSize(this, 980, 640);

        if (AdminService.IsAdministrator)
        {
            Title = "NeoUninstaller (administrateur)";
            AdminTag.Visibility = Visibility.Visible;
        }

        SetTheme(AppSettings.Current.Theme);

        // Surveillance du démarrage : une alerte quand un programme s'y ajoute
        _watcher.Added += items => DispatcherQueue.TryEnqueue(() =>
        {
            WatchBar.Title = items.Count == 1
                ? "Un programme s'est ajouté au démarrage"
                : $"{items.Count} programmes se sont ajoutés au démarrage";
            WatchBar.Message = string.Join(", ", items.Take(3).Select(i => i.DisplayName)) + (items.Count > 3 ? "…" : "");
            WatchBar.IsOpen = true;
        });
        Closed += (_, _) => _watcher.Dispose();

        NavView.Loaded += (_, _) => NavigateTo(startPage is not null && Pages.ContainsKey(startPage) ? startPage : "Programs");

        // NeoSuite affiche l'état de NeoUninstaller et ses actions (clic droit sur sa tuile)
        NeoIpc.StartServer("NeoUninstaller", () => OnUiThread(DescribeForSuite), command => OnUiThread(() => RunSuiteCommand(command)));
    }

    public void SetTheme(int theme) => RootGrid.RequestedTheme = theme switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public void NavigateTo(string tag)
    {
        if (tag == "Settings")
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
    }

    /// <summary>Ouvre l'assistant de désinstallation (ou d'analyse forcée) pour un programme.</summary>
    /// <param name="programs">Un <see cref="InstalledProgram"/> ou une liste (désinstallation en lot).</param>
    public void StartUninstall(object programs)
    {
        // L'assistant n'est pas un élément du menu : on désélectionne « Programmes » sans changer de page
        NavView.SelectedItem = null;
        ContentFrame.Navigate(typeof(UninstallPage), programs, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    public void BringToFront()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void WatchBar_Click(object sender, RoutedEventArgs e)
    {
        WatchBar.IsOpen = false;
        NavigateTo("Startup");
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null || !Pages.TryGetValue(tag, out var page) || ContentFrame.CurrentSourcePageType == page)
            return;
        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    // ---------- Intégration NeoSuite ----------

    private Task<NeoAppInfo> DescribeForSuite()
    {
        var count = AppState.Programs.Count;
        NeoAction[] actions =
        [
            new("open", "Ouvrir NeoUninstaller", ""),
            new("page:Programs", "Programmes installés", ""),
            new("page:WindowsApps", "Applis Windows", ""),
            new("page:Startup", "Démarrage de Windows", ""),
            new("page:Monitor", "Moniteur d'installation", ""),
            new("page:Drivers", "Pilotes orphelins", ""),
            new("page:Cleaner", "Nettoyage", ""),
            new("page:History", "Historique et sauvegardes", ""),
            new("page:Settings", "Paramètres", ""),
            new("quit", "Quitter NeoUninstaller", ""),
        ];
        return Task.FromResult(new NeoAppInfo(count > 0 ? $"{count} programmes" : null, actions));
    }

    private Task<NeoActionResult?> RunSuiteCommand(string command)
    {
        switch (command)
        {
            case "open":
                BringToFront();
                break;
            case "quit":
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Close);
                break;
            default:
                if (command.StartsWith("page:"))
                {
                    BringToFront();
                    NavigateTo(command["page:".Length..]);
                }
                break;
        }
        return Task.FromResult<NeoActionResult?>(null);
    }

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
