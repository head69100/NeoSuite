using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.Shared;
using NeoBoost.Services;
using NeoBoost.Views;
using Windows.Graphics;

namespace NeoBoost;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Processes"] = typeof(ProcessesPage),
        ["History"] = typeof(HistoryPage),
        ["Health"] = typeof(HealthPage),
        ["Power"] = typeof(PowerPage),
        ["Tweaks"] = typeof(TweaksPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private readonly WindowMinSize _minSize;

    public MainWindow(string? startPage = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1180, 800));
        _minSize = new WindowMinSize(this, 900, 620);

        if (AdminService.IsAdministrator)
        {
            Title = "NeoBoost (administrateur)";
            AdminTag.Visibility = Visibility.Visible;
        }

        SetTheme(AppSettings.Current.Theme);
        NavView.Loaded += (_, _) => NavigateTo(startPage is not null && Pages.ContainsKey(startPage) ? startPage : "Dashboard");

        // L'historique de consommation se remplit dès l'ouverture, même sans visiter la page
        UsageHistory.Start();

        // NeoSuite affiche l'état de NeoBoost et ses actions (clic droit sur sa tuile)
        NeoIpc.StartServer("NeoBoost", () => OnUiThread(DescribeForSuite), command => OnUiThread(() => RunSuiteCommand(command)));
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

    public void BringToFront()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void Quit() => Application.Current.Exit();

    /// <summary>Le mode Boost a changé (depuis NeoSuite) : la page affichée se met à jour.</summary>
    public event EventHandler? BoostChanged;

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
        var boost = PowerService.IsBoostActive;
        NeoAction[] actions =
        [
            new("open", "Ouvrir NeoBoost", ""),
            boost ? new("boost:off", "Désactiver le mode Boost", "") : new("boost:on", "Activer le mode Boost", ""),
            new("page:Processes", "Programmes actifs", ""),
            new("page:History", "Historique de consommation", ""),
            new("page:Health", "État du PC", ""),
            new("page:Power", "Alimentation", ""),
            new("page:Tweaks", "Réglages Windows", ""),
            new("quit", "Quitter NeoBoost", ""),
        ];
        return Task.FromResult(new NeoAppInfo(boost ? "Boost actif" : null, actions));
    }

    private Task<NeoActionResult?> RunSuiteCommand(string command)
    {
        NeoActionResult? result = null;
        switch (command)
        {
            case "open":
                BringToFront();
                break;
            case "boost:on":
            case "boost:off":
                try
                {
                    if (command == "boost:on")
                    {
                        var message = PowerService.EnableBoost();
                        result = new(PowerService.IsBoostActive ? "Mode Boost activé" : "Déjà en performances maximales", message);
                    }
                    else
                    {
                        PowerService.DisableBoost();
                        result = new("Mode Boost désactivé", "Le mode d'alimentation précédent est rétabli.");
                    }
                }
                catch (Exception ex)
                {
                    result = new("Mode Boost impossible", ex.Message);
                }
                BoostChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "quit":
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Quit);
                break;
            default:
                if (command.StartsWith("page:"))
                {
                    BringToFront();
                    NavigateTo(command["page:".Length..]);
                }
                break;
        }
        return Task.FromResult(result);
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
