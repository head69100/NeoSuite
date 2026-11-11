using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Programmes fantômes : encore listés par Windows alors que leur désinstalleur et leur dossier n'existent plus
/// (suppression à la main, disque changé…). Ils ne peuvent plus être désinstallés normalement.
/// </summary>
public static class GhostPrograms
{
    public static List<InstalledProgram> Find(IEnumerable<InstalledProgram> programs) =>
        programs.Where(IsGhost).ToList();

    public static bool IsGhost(InstalledProgram program)
    {
        if (program.IsForced)
            return false;
        try
        {
            var (file, _) = UninstallRunner.BaseCommand(program);
            if (!Path.IsPathRooted(file) || File.Exists(file))
                return false; // msiexec, cmd… ou désinstalleur présent
            var folder = LeftoverScanner.ResolveInstallDir(program);
            return folder is null || !Directory.Exists(folder);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Retire les entrées de la liste (clé Uninstall sauvegardée avant, restaurable depuis l'Historique).</summary>
    public static Task<CleanReport> RemoveAsync(IReadOnlyList<InstalledProgram> ghosts, bool isAdmin)
    {
        var items = ghosts.Select(p => new Leftover
        {
            Kind = LeftoverKind.RegistryKey,
            Path = p.UninstallKeyPath,
            Reason = "Programme fantôme",
            Confidence = Confidence.Certain,
            RequiresAdmin = !p.IsPerUser,
            CanDelete = p.IsPerUser || isAdmin,
        }).Where(l => l.CanDelete).ToList();
        return LeftoverCleaner.CleanAsync("Programmes fantômes", items);
    }
}