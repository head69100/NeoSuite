using Microsoft.UI.Xaml;
using Neo.Shared;
using NeoRar.Services;

namespace NeoRar;

/// <summary>
/// Plusieurs NeoRar peuvent tourner en même temps (une fenêtre par archive, comme WinRAR).
/// Arguments reconnus (envoyés par le clic droit de l'Explorateur ou le planificateur de tâches) :
///   &lt;archive&gt;                    ouvre l'archive
///   --extract-here &lt;archive&gt;     extrait à côté de l'archive
///   --extract-folder &lt;archive&gt;   extrait dans un dossier du nom de l'archive
///   --add &lt;fichier ou dossier&gt;   crée une archive (les sélections multiples sont regroupées)
///   --backup &lt;identifiant&gt;       exécute une sauvegarde planifiée (lancée par Windows)
///   --register-shell            active l'intégration à l'Explorateur (appelé après l'installation)
///   --unregister-shell          retire l'intégration à l'Explorateur (appelé à la désinstallation)
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var mode = cmdArgs.FirstOrDefault(a => a.StartsWith("--"));
        var paths = cmdArgs.Where(a => !a.StartsWith("--")).ToList();

        switch (mode)
        {
            case "--unregister-shell":
                ShellIntegration.Disable();
                Exit();
                return;

            case "--register-shell":
                // Lancé par NeoSuite juste après l'installation
                SetUpShellIntegration();
                Exit();
                return;

            case "--extract-here" when paths.Count > 0:
            {
                var archive = paths[0];
                TaskWindow.ShowExtract(archive, Path.GetDirectoryName(archive)!, autoClose: true, checkConflicts: true);
                return;
            }

            case "--extract-folder" when paths.Count > 0:
            {
                var archive = paths[0];
                var folder = Path.Combine(Path.GetDirectoryName(archive)!, ArchiveService.FolderNameFor(archive));
                TaskWindow.ShowExtract(archive, ArchiveService.UniquePath(folder, isDirectory: true), autoClose: true);
                return;
            }

            case "--add" when paths.Count > 0:
                if (AddCollector.Collect(paths) is { } all)
                    TaskWindow.ShowCreate(all);
                else
                    Exit(); // les fichiers ont été confiés au premier NeoRar
                return;

            case "--backup" when cmdArgs.Length >= 2 && Guid.TryParse(cmdArgs[1], out var backupId):
                if (BackupService.Find(backupId) is { } job)
                    TaskWindow.ShowBackup(job, autoClose: true); // exécution planifiée : bulle + fermeture au résultat
                else
                    Exit(); // la sauvegarde a été supprimée entre-temps
                return;
        }

        SetUpShellIntegration();
        _ = Task.Run(CleanTemp);
        new MainWindow(paths.FirstOrDefault()).Activate();
    }

    /// <summary>
    /// Fichiers extraits pour être ouverts ou glissés hors de l'archive (%TEMP%\NeoRar) : supprimés après un jour,
    /// le temps que les programmes qui les ont ouverts et l'Explorateur qui les a copiés en aient fini.
    /// </summary>
    private static void CleanTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "NeoRar");
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (DateTime.Now - Directory.GetCreationTime(dir) > TimeSpan.FromDays(1))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // Fichier encore ouvert : on réessaiera au prochain lancement
            }
        }
    }

    /// <summary>
    /// Première ouverture de la copie installée : intégration à l'Explorateur activée d'office (comme WinRAR),
    /// puis remise à jour si NeoRar a changé d'emplacement. Désactivable dans les paramètres.
    /// </summary>
    private static void SetUpShellIntegration()
    {
        if (!ElevatedLaunch.IsInstalledCopy("NeoRar"))
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
            // Sans intégration, NeoRar reste utilisable depuis sa fenêtre
        }
    }
}
