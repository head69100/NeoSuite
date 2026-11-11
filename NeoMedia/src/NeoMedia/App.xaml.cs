using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Neo.Shared;
using NeoMedia.Services;

namespace NeoMedia;

/// <summary>
/// Plusieurs NeoMedia peuvent tourner en même temps, comme VLC. Arguments reconnus :
///   &lt;fichier, dossier ou lien&gt;…   lus à la suite (les sélections multiples de l'Explorateur sont regroupées)
///   --register-shell                 active « Ouvrir avec » (appelé après l'installation)
///   --unregister-shell               le retire (appelé à la désinstallation)
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        Diag.Log("App() : premiere construction de l'application");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diag.Log("OnLaunched : demarrage");
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

        if (cmdArgs.Contains("--unregister-shell"))
        {
            ShellIntegration.Disable();
            Exit();
            return;
        }
        if (cmdArgs.Contains("--register-shell"))
        {
            SetUpShellIntegration();
            Exit();
            return;
        }

        var paths = cmdArgs.Where(a => !a.StartsWith("--")).ToList();
        List<string> files = [];
        if (paths.Count > 0)
        {
            if (FileCollector.Collect(paths) is not { } all)
            {
                Exit(); // les fichiers ont été confiés au premier NeoMedia lancé
                return;
            }
            files = MediaFiles.Expand(all);
        }

        // Charge le moteur de VLC (libvlc\win-x64, à côté de l'exécutable)
        Core.Initialize();
        Diag.Log($"libvlc initialise, {files.Count} fichier(s) a lire");
        SetUpShellIntegration();
        Diag.Log("integration Explorateur verifiee, creation de la fenetre");
        new MainWindow(files).Activate();
        Diag.Log("fenetre creee et activee");
    }

    /// <summary>Copie installée : « Ouvrir avec » activé d'office, puis tenu à jour si NeoMedia change de place.</summary>
    private static void SetUpShellIntegration()
    {
        if (!ElevatedLaunch.IsInstalledCopy("NeoMedia"))
            return;
        var settings = AppSettings.Current;
        try
        {
            if (settings.ShellIntegration is null)
            {
                ShellIntegration.Enable();
                settings.ShellIntegration = true;
                settings.Save();
            }
            else if (settings.ShellIntegration == true && !ShellIntegration.IsEnabled)
            {
                ShellIntegration.Enable();
            }
        }
        catch (Exception)
        {
            // Sans intégration, NeoMedia reste utilisable depuis sa fenêtre
        }
    }
}
