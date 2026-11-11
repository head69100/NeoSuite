using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Neo.Shared;

namespace NeoSuite.Services;

/// <summary>
/// Une version de NeoSuite plus récente que celle qui tourne, et l'installateur qui l'apporte :
/// déjà sur cet ordinateur (<see cref="InstallerPath"/>), ou à télécharger depuis GitHub (<see cref="Release"/>).
/// </summary>
public sealed record SuiteUpdate(Version Version, string? InstallerPath, string FolderLabel, IReadOnlyList<string> Notes, ReleaseInfo? Release = null)
{
    public bool IsOnline => InstallerPath is null && Release is not null;

    /// <summary>Taille à télécharger (installateur + données), pour l'afficher avant de lancer.</summary>
    public long DownloadSize => Release is null ? 0
        : (Release.Asset(UpdateService.InstallerName)?.Size ?? 0) + (Release.Asset(UpdateService.PayloadName)?.Size ?? 0);
}

/// <summary>
/// Cherche une mise à jour de NeoSuite :
/// 1. sur cet ordinateur : un installateur NeoSuite*.exe (avec son NeoSuite.pak) plus récent dans Téléchargements,
///    sur le Bureau, dans Documents, ou dans le dossier <c>dist</c> du développeur (rien à télécharger) ;
/// 2. en ligne : la dernière release GitHub (<see cref="GitHubReleases"/>), téléchargée au moment d'installer.
/// </summary>
public static class UpdateService
{
    public const string InstallerName = "NeoSuite.exe";
    public const string PayloadName = "NeoSuite.pak";

    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    public static Version CurrentVersion { get; } =
        InstallerPackage.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    /// <summary>Résultat de la dernière recherche (null : à jour, ou pas encore cherché).</summary>
    public static SuiteUpdate? Available { get; private set; }

    public static DateTime? LastCheck { get; private set; }

    /// <summary>Recherche locale puis en ligne, en arrière-plan (jamais bloquante pour l'interface).</summary>
    public static async Task<SuiteUpdate?> CheckAsync(bool force = false)
    {
        var local = await Task.Run(FindLocalUpdate);
        var online = await FindOnlineUpdateAsync(force);

        // Un installateur déjà présent sur le PC est préféré à version égale : rien à télécharger
        Available = online is not null && (local is null || online.Version > local.Version) ? online : local;
        LastCheck = DateTime.Now;
        return Available;
    }

    private static async Task<SuiteUpdate?> FindOnlineUpdateAsync(bool force)
    {
        var release = await GitHubReleases.GetLatestAsync(force);
        if (release is null || release.Version <= CurrentVersion
            || release.Asset(InstallerName) is null || release.Asset(PayloadName) is null)
            return null;
        return new SuiteUpdate(release.Version, null, "GitHub", release.Notes, release);
    }

    /// <summary>
    /// Mise à jour en ligne : télécharge l'installateur et ses données (progression de 0 à 1) dans
    /// %LocalAppData%\NeoSuite\Updates\&lt;version&gt;, puis renvoie la mise à jour prête à lancer.
    /// </summary>
    public static async Task<SuiteUpdate> DownloadAsync(SuiteUpdate update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (!update.IsOnline)
            return update;
        var release = update.Release!;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoSuite", "Updates", update.Version.ToString(3));
        var exe = release.Asset(InstallerName)!;
        var pak = release.Asset(PayloadName)!;
        var total = (double)(exe.Size + pak.Size);

        // Progression globale : l'installateur (petit) puis ses données (gros)
        await GitHubReleases.DownloadAsync(exe, Path.Combine(folder, InstallerName),
            new Progress<double>(p => progress?.Report(p * exe.Size / total)), cancel);
        await GitHubReleases.DownloadAsync(pak, Path.Combine(folder, PayloadName),
            new Progress<double>(p => progress?.Report((exe.Size + p * pak.Size) / total)), cancel);

        var ready = update with { InstallerPath = Path.Combine(folder, InstallerName), FolderLabel = "GitHub" };
        Available = ready;
        return ready;
    }

    /// <summary>Ouvre l'installateur : Windows demande la confirmation administrateur, puis il met NeoSuite à jour.</summary>
    public static void Launch(SuiteUpdate update)
    {
        if (update.InstallerPath is null)
            throw new InvalidOperationException("L'installateur n'est pas encore téléchargé.");
        Process.Start(new ProcessStartInfo(update.InstallerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(update.InstallerPath),
        });
    }

    private static SuiteUpdate? FindLocalUpdate()
    {
        var self = Environment.ProcessPath;
        SuiteUpdate? best = null;

        foreach (var (folder, label) in SearchFolders())
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(folder, "NeoSuite*.exe");
            }
            catch (Exception)
            {
                continue; // dossier absent ou inaccessible
            }

            foreach (var exe in candidates)
            {
                // Ni nous-mêmes, ni la copie installée
                if (string.Equals(exe, self, StringComparison.OrdinalIgnoreCase)
                    || exe.StartsWith(AppCatalog.InstallRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    // Un NeoSuite*.exe quelconque (ex. l'application elle-même) n'a pas de données : Open renvoie null
                    using var package = InstallerPackage.Open(exe);
                    if (package is null)
                        continue;
                    var info = package.ReadInfo();
                    if (info.SuiteVersion > CurrentVersion && (best is null || info.SuiteVersion > best.Version))
                        best = new SuiteUpdate(info.SuiteVersion, exe, label, info.Notes);
                }
                catch (Exception)
                {
                    // Fichier illisible, incomplet ou en cours de téléchargement : on l'ignore
                }
            }
        }
        return best;
    }

    /// <summary>Téléchargements, Bureau, Documents (et leurs sous-dossiers « Neo… »), puis le dossier de développement.</summary>
    private static IEnumerable<(string Folder, string Label)> SearchFolders()
    {
        var roots = new List<(string? Path, string Label)>
        {
            (KnownFolder(DownloadsFolder), "Téléchargements"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "le Bureau"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, label) in roots)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || !seen.Add(path))
                continue;
            yield return (path, label);

            string[] sub;
            try
            {
                sub = Directory.GetDirectories(path, "Neo*");
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var dir in sub.Where(seen.Add))
                yield return (dir, $"{label} › {System.IO.Path.GetFileName(dir)}");
        }

        if (AppCatalog.FindSuiteRoot() is { } suiteRoot && Path.Combine(suiteRoot, "dist") is var dist && Directory.Exists(dist) && seen.Add(dist))
            yield return (dist, "le dossier dist");
    }

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
