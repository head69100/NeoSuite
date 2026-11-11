using Microsoft.UI.Xaml;
using Neo.Shared;
using NeoSuite.Services;

namespace NeoSuite;

public partial class App : Application
{
    private readonly SingleInstance _singleInstance = new("NeoSuite");
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Relance en administrateur pour (dés)installer une appli : pas de fenêtre, juste le travail
        if (InstallWorker.TryRun(Environment.GetCommandLineArgs()) is { } exitCode)
        {
            Environment.Exit(exitCode);
            return;
        }

        // Déjà lancé (dans la barre système) : on demande simplement à l'instance existante d'ouvrir le panneau
        if (!_singleInstance.TryAcquire())
        {
            _singleInstance.SignalExisting();
            Exit();
            return;
        }

        // NeoSoft s'en sert pour installer les applis Neo de son catalogue
        NeoSuiteInfo.Register(Environment.ProcessPath!, AppCatalog.CatalogPath);

        _window = new MainWindow();
        var window = _window;
        _singleInstance.ListenForShowRequests(() => window.DispatcherQueue.TryEnqueue(() => window.OpenPanel()));

        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains(StartupService.BackgroundArg))
        {
            // Démarrage de Windows : NeoSuite attend dans la barre système
            if (!AppSettings.Current.WelcomeShown)
                window.ShowWelcomeBalloon();
        }
        else
        {
            window.OpenPanel(showSettings: cmdArgs.Contains("--settings"));
        }

        if (!AppSettings.Current.WelcomeShown)
        {
            AppSettings.Current.WelcomeShown = true;
            AppSettings.Current.Save();
        }
    }
}
