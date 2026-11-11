using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.Shared;
using NeoSoft.Services;
using NeoSoft.Views;
using Windows.Graphics;

namespace NeoSoft;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Catalog"] = typeof(CatalogPage),
        ["Installed"] = typeof(InstalledPage),
        ["Activity"] = typeof(ActivityPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private readonly WindowMinSize _minSize;

    public MainWindow(string? startPage = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1240, 820));
        _minSize = new WindowMinSize(this, 900, 620);

        if (ElevatedLaunch.IsAdministrator)
        {
            Title = "NeoSoft (administrateur)";
            AdminTag.Visibility = Visibility.Visible;
        }

        SetTheme(AppSettings.Current.Theme);
        NavView.Loaded += (_, _) => NavigateTo(startPage is not null && Pages.ContainsKey(startPage) ? startPage : "Catalog");

        SoftwareManager.Changed += (_, _) => UpdateBadges();
        if (AppSettings.Current.CheckUpdatesOnStartup)
            _ = SoftwareManager.RefreshAsync();

        // Retour sur NeoSoft : un logiciel a pu être installé ou désinstallé ailleurs entre-temps (NeoUninstaller…)
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated
                && SoftwareManager.PendingCount == 0
                && SoftwareManager.LastRefresh is { } last && DateTime.Now - last > TimeSpan.FromSeconds(10))
                _ = SoftwareManager.RefreshAsync();
        };

        // NeoSuite affiche l'état de NeoSoft et ses actions (clic droit sur sa tuile)
        NeoIpc.StartServer("NeoSoft", () => OnUiThread(DescribeForSuite), command => OnUiThread(() => RunSuiteCommand(command)));

        // Ferme-t-on pendant une installation ? winget continuerait seul, sans suivi : on prévient
        AppWindow.Closing += AppWindow_Closing;

        // Impr. écran ne fonctionne pas quand une appli admin est au premier plan (le shell ne la voit
        // pas) : on reprend la touche et on déclenche nous-mêmes le volet de capture système.
        PrintScreenFix.Install(this);
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

    public void Quit()
    {
        _quitting = true;
        PrintScreenFix.Uninstall();
        Application.Current.Exit();
    }

    private bool _quitting;

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_quitting || SoftwareManager.PendingCount == 0)
            return;
        args.Cancel = true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
            Title = "Des installations sont en cours",
            Content = "Si vous quittez maintenant, les logiciels en attente ne seront pas installés.",
            PrimaryButtonText = "Quitter quand même",
            CloseButtonText = "Continuer",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Quit();
    }

    private void UpdateBadges()
    {
        var updates = SoftwareManager.UpdateCount;
        UpdatesBadge.Value = updates;
        UpdatesBadge.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
        var pending = SoftwareManager.PendingCount;
        PendingBadge.Value = pending;
        PendingBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var pending = SoftwareManager.PendingCount;
        var updates = SoftwareManager.UpdateCount;
        var status = pending > 0 ? $"{pending} en cours"
            : updates == 1 ? "1 mise à jour"
            : updates > 1 ? $"{updates} mises à jour"
            : null;

        var actions = new List<NeoAction>
        {
            new("open", "Ouvrir NeoSoft", ""),
            new("page:Catalog", "Catalogue", ""),
            new("page:Installed", "Mises à jour", ""),
        };
        if (updates > 0)
            actions.Add(new("updateall", $"Tout mettre à jour ({updates})", ""));
        actions.Add(new("quit", "Quitter NeoSoft", ""));
        return Task.FromResult(new NeoAppInfo(status, actions));
    }

    private Task<NeoActionResult?> RunSuiteCommand(string command)
    {
        NeoActionResult? result = null;
        switch (command)
        {
            case "open":
                BringToFront();
                break;
            case "updateall":
                var count = SoftwareManager.EnqueueAllUpdates();
                result = new("Mises à jour lancées", count == 1 ? "1 logiciel va être mis à jour." : $"{count} logiciels vont être mis à jour.");
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
