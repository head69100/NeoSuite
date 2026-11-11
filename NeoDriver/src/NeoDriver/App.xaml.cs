using Microsoft.UI.Xaml;
using Neo.Shared;
using NeoDriver.Services;

namespace NeoDriver;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>Mises à jour à installer dès le démarrage (après une relance en administrateur).</summary>
    public static HashSet<string>? PendingInstallIds { get; set; }

    private readonly SingleInstance _singleInstance = new("NeoDriver");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Recherche silencieuse lancée par NeoSuite chaque semaine : pas de fenêtre, juste le résultat pour sa tuile
        if (Environment.GetCommandLineArgs().Contains("--scan-only"))
        {
            _ = ScanOnlyAsync();
            return;
        }
        // Démarrage administrateur activé (depuis NeoSuite) : on se relance via la tâche planifiée, sans UAC
        if (ElevatedLaunch.HandleStartup("NeoDriver") is not { } allArgs)
        {
            Exit();
            return;
        }

        // Déjà ouvert : on réaffiche cette instance-là
        if (!_singleInstance.TryAcquire(waitForPrevious: allArgs.Contains("--relaunched")))
        {
            _singleInstance.SignalExisting();
            Exit();
            return;
        }

        string? startPage = null;
        foreach (var arg in allArgs)
        {
            if (arg.StartsWith("--page="))
                startPage = arg["--page=".Length..];
            else if (arg.StartsWith("--install="))
                PendingInstallIds = arg["--install=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            else if (arg == "--enable-auto-admin" && AdminService.IsAdministrator && ElevatedLaunch.IsInstalledCopy("NeoDriver"))
                _ = Task.Run(() =>
                {
                    try
                    {
                        ElevatedLaunch.Register("NeoDriver");
                    }
                    catch (Exception)
                    {
                        // Sans tâche, Windows redemandera simplement confirmation au prochain lancement
                    }
                });
        }

        MainWindow = new MainWindow(startPage);
        MainWindow.Activate();
        // Relancée par la tâche planifiée : Windows refuse le premier plan, on le force
        ForegroundWindow.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow));

        var window = MainWindow;
        _singleInstance.ListenForShowRequests(() => window.DispatcherQueue.TryEnqueue(window.ShowWindow));
    }

    /// <summary>
    /// Cherche les mises à jour de pilotes sans ouvrir de fenêtre, puis ferme. Le résultat est publié pour NeoSuite
    /// (DriverUpdateService.PublishStatus). Ne fait rien si NeoDriver est déjà ouvert : son état est alors à jour.
    /// </summary>
    private async Task ScanOnlyAsync()
    {
        using var scanning = new Mutex(false, @"Local\NeoDriver.ScanOnly");
        try
        {
            var openElsewhere = System.Diagnostics.Process.GetProcessesByName("NeoDriver").Any(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (!openElsewhere && scanning.WaitOne(0))
            {
                try
                {
                    await DriverUpdateService.SearchAsync();
                }
                finally
                {
                    scanning.ReleaseMutex();
                }
            }
        }
        catch (Exception)
        {
            // Pas de réponse de Windows Update : la tuile garde son dernier état
        }
        finally
        {
            Exit();
        }
    }
}
