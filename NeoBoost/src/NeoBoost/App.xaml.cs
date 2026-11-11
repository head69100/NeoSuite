using Microsoft.UI.Xaml;
using Neo.Shared;

namespace NeoBoost;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    private readonly SingleInstance _singleInstance = new("NeoBoost");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Démarrage administrateur activé (depuis NeoSuite) : on se relance via la tâche planifiée, sans UAC
        if (ElevatedLaunch.HandleStartup("NeoBoost") is not { } cmdArgs)
        {
            Exit();
            return;
        }

        // Déjà ouvert : on réaffiche cette fenêtre-là
        if (!_singleInstance.TryAcquire(waitForPrevious: cmdArgs.Contains("--relaunched")))
        {
            _singleInstance.SignalExisting();
            Exit();
            return;
        }

        string? startPage = null;
        foreach (var arg in cmdArgs)
        {
            if (arg.StartsWith("--page="))
                startPage = arg["--page=".Length..];
        }

        MainWindow = new MainWindow(startPage);
        MainWindow.Activate();
        // Relancée par la tâche planifiée : Windows refuse le premier plan, on le force
        ForegroundWindow.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow));

        var window = MainWindow;
        _singleInstance.ListenForShowRequests(() => window.DispatcherQueue.TryEnqueue(window.BringToFront));
    }
}
