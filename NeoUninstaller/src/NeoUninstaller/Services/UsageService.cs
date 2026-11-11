using System.Text.RegularExpressions;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Dernière utilisation d'un programme, déduite du dossier Prefetch de Windows (un fichier par exécutable lancé).
/// Ce dossier n'est lisible qu'en administrateur : sans cela, l'information reste « inconnue ».
/// </summary>
public static class UsageService
{
    public static bool Available { get; private set; }

    public static Task LoadAsync(IEnumerable<InstalledProgram> programs) => Task.Run(() =>
    {
        var lastRun = ReadPrefetch();
        Available = lastRun.Count > 0;
        if (!Available)
            return;

        foreach (var program in programs)
        {
            if (LooksLikeDriver(program) || IsSystemComponent(program))
            {
                program.UsageNotApplicable = true;
                continue;
            }
            var exes = ExecutableNames(program).ToList();
            // Sans exécutable identifiable (entrée de registre sans dossier ni icône .exe),
            // on ne peut rien savoir : on n'affiche surtout pas « jamais utilisé ».
            if (exes.Count == 0)
            {
                program.UsageNotApplicable = true;
                continue;
            }
            DateTime? best = null;
            foreach (var exe in exes)
            {
                if (lastRun.TryGetValue(exe, out var date) && (best is null || date > best))
                    best = date;
            }
            program.LastUsed = best;
        }
    });

    /// <summary>
    /// Un pilote (NVIDIA, Realtek, AMD…) n'est pas un programme que l'utilisateur lance : il est chargé par Windows.
    /// Son absence dans Prefetch ne veut rien dire, on ne lui affiche donc jamais de badge « jamais utilisé ».
    /// </summary>
    private static bool LooksLikeDriver(InstalledProgram program)
    {
        var name = program.DisplayName ?? "";
        var publisher = program.Publisher ?? "";
        foreach (var keyword in DriverKeywords)
        {
            if (name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                || publisher.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }

        // Dossier d'installation contenant des fichiers de pilote (.inf/.sys) : c'en est un
        try
        {
            var dir = LeftoverScanner.ResolveInstallDir(program);
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (file.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    private static readonly string[] DriverKeywords =
    [
        "driver", "pilote", "chipset", "gpu driver", "display driver", "audio driver",
    ];

    /// <summary>
    /// Mots-clés d'éléments dont d'autres programmes dépendent (ou que Windows installe lui-même) :
    /// les juger « jamais utilisé » serait trompeur — ce ne sont pas des programmes que l'on lance.
    /// </summary>
    private static readonly string[] ComponentKeywords =
    [
        "redistributable", "runtime", "framework", ".net", "sdk", "anti-cheat", "anticheat",
        "visual c++", "visual studio", "fix database", "composant", "component",
        "install manager", "service pack", "update for", "mise à jour de",
    ];

    /// <summary>« … (KB5012345) » : correctif Windows.</summary>
    private static readonly Regex KbRegex = new(@"\(KB\d{6,}\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Composant système ou bibliothèque partagée (Visual C++ Redistributable, .NET SDK, Anti-Cheat,
    /// correctifs Windows…) : personne ne le « lance », aucun badge d'inutilisation ne s'applique.
    /// </summary>
    private static bool IsSystemComponent(InstalledProgram program)
    {
        var name = program.DisplayName ?? "";
        foreach (var keyword in ComponentKeywords)
            if (name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                return true;

        // Correctifs Windows : « … (KB5012345) »
        return KbRegex.IsMatch(name);
    }

    private static Dictionary<string, DateTime> ReadPrefetch()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            foreach (var file in Directory.EnumerateFiles(folder, "*.pf"))
            {
                var name = Path.GetFileName(file);
                var dash = name.LastIndexOf('-');
                if (dash <= 0)
                    continue;
                var exe = name[..dash];
                var date = File.GetLastWriteTime(file);
                if (!result.TryGetValue(exe, out var known) || date > known)
                    result[exe] = date;
            }
        }
        catch (Exception)
        {
            // Accès refusé ou Prefetch désactivé
        }
        return result;
    }

    private static IEnumerable<string> ExecutableNames(InstalledProgram program)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
        {
            var (file, _) = IconService.ParseIconLocation(program.DisplayIcon);
            if (file?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
                && !IsInstallerName(Path.GetFileName(file)))
                names.Add(Path.GetFileName(file));
        }
        try
        {
            var dir = LeftoverScanner.ResolveInstallDir(program);
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(exe);
                    if (!name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !IsInstallerName(name))
                        names.Add(name);
                }
            }
        }
        catch (Exception)
        {
        }
        return names.Select(n => n.ToUpperInvariant());
    }

    /// <summary>
    /// Un exécutable qui n'est que l'installateur ou le programme de mise à jour (Setup.exe, *Installer.exe…)
    /// ne dit rien de l'utilisation réelle du programme : on l'ignore.
    /// </summary>
    private static bool IsInstallerName(string fileName) =>
        fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("installer", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("setup", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("update", StringComparison.OrdinalIgnoreCase);
}