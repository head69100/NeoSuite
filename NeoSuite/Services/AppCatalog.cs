using System.Diagnostics;
using System.Text.Json;
using Neo.Shared;
using NeoSuite.Models;

namespace NeoSuite.Services;

/// <summary>
/// La bibliothèque des applications Neo. Pour ajouter une appli à la suite :
/// une entrée ici, son icône dans Assets\Apps, puis tools\build-packages.ps1 -Apps &lt;App&gt;.
/// </summary>
public static class AppCatalog
{
    /// <summary>Dossier d'installation des applis Neo : C:\Program Files\Neo\&lt;App&gt;.</summary>
    public static string InstallRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neo");

    public static string InstallDir(string id) => Path.Combine(InstallRoot, id);

    /// <summary>Fichier du dossier d'installation qui contient le SHA-256 du paquet installé.</summary>
    public const string PackageHashFile = "neo-package.sha256";

    public static List<NeoApp> Load()
    {
        List<NeoApp> apps =
        [
            new()
            {
                Id = "NeoDriver",
                Name = "NeoDriver",
                Description = "Mises à jour et installation des pilotes",
                Category = "Système",
                Icon = "NeoDriver.png",
                BackgroundCheckArg = "--scan-only",
            },
            new()
            {
                Id = "NeoUninstaller",
                Name = "NeoUninstaller",
                Description = "Désinstallation complète des programmes et de leurs restes",
                Category = "Nettoyage",
                Icon = "NeoUninstaller.png",
            },
            new()
            {
                Id = "NeoBoost",
                Name = "NeoBoost",
                Description = "Mode Boost, programmes gourmands et réglages qui accélèrent vraiment",
                Category = "Performances",
                Icon = "NeoBoost.png",
            },
            new()
            {
                Id = "NeoSound",
                Name = "NeoSound",
                Description = "Duplique le son du PC vers deux casques en même temps (filaire + Bluetooth)",
                Category = "Audio",
                Icon = "NeoSound.png",
            },
            new()
            {
                Id = "NeoSoft",
                Name = "NeoSoft",
                Description = "Installe et met à jour vos logiciels depuis leurs sources officielles",
                Category = "Logiciels",
                Icon = "NeoSoft.png",
            },
            new()
            {
                Id = "NeoTorrent",
                Name = "NeoTorrent",
                Description = "Lit les fichiers .torrent et les liens magnet, télécharge et partage",
                Category = "Téléchargements",
                Icon = "NeoTorrent.png",
            },
        ];

        var packages = LoadPackageCatalog();
        foreach (var app in apps)
        {
            Refresh(app);
            if (packages?.TryGetValue(app.Id, out var info) == true && File.Exists(info.Path))
            {
                app.PackageSource = info.Path;
                app.AvailableVersion = Version.TryParse(info.Version, out var v) ? v : null;
                app.PackageSize = info.Size;
                app.PackageSha256 = info.Sha256;
            }
        }
        return apps;
    }

    /// <summary>Relit l'état d'installation sur le disque (après une installation ou une désinstallation).</summary>
    public static void Refresh(NeoApp app)
    {
        var exe = Path.Combine(InstallDir(app.Id), $"{app.Id}.exe");
        app.ExecutablePath = File.Exists(exe) ? exe : null;
        app.InstalledVersion = app.ExecutablePath is null ? null
            : Version.TryParse(FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0], out var v) ? v : null;
        var status = app.ExecutablePath is null ? null : NeoStatus.Read(app.Id);
        // Un état de plus d'un mois n'est plus fiable : mieux vaut ne rien afficher
        app.StoredStatus = status is not null && DateTime.Now - status.Updated < TimeSpan.FromDays(30) ? status.Text : null;
        var hashFile = Path.Combine(InstallDir(app.Id), PackageHashFile);
        try
        {
            app.InstalledPackageSha256 = app.ExecutablePath is not null && File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : null;
        }
        catch (IOException)
        {
            app.InstalledPackageSha256 = null;
        }
        app.NotifyStateChanged();
    }

    private static readonly Dictionary<string, DateTime> BackgroundAttempts = [];

    /// <summary>
    /// Fait chercher en silence les applis qui savent le faire (NeoDriver : mises à jour de pilotes) quand leur dernier
    /// état date de plus d'une semaine, au plus une fois par heure et par appli. Le résultat arrive sur la tuile
    /// (<paramref name="onDone"/> est appelé à la fin de la recherche, sur un autre thread).
    /// </summary>
    public static void StartBackgroundChecks(IEnumerable<NeoApp> apps, Action<NeoApp> onDone)
    {
        foreach (var app in apps)
        {
            if (app.BackgroundCheckArg is not { } arg || app.ExecutablePath is null || app.IsRunning)
                continue;
            var last = NeoStatus.Read(app.Id)?.Updated;
            if (last is { } updated && DateTime.Now - updated < TimeSpan.FromDays(7))
                continue;
            if (BackgroundAttempts.TryGetValue(app.Id, out var attempt) && DateTime.Now - attempt < TimeSpan.FromHours(1))
                continue;
            BackgroundAttempts[app.Id] = DateTime.Now;

            try
            {
                var process = Process.Start(new ProcessStartInfo(app.ExecutablePath, arg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath),
                });
                if (process is null)
                    continue;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    process.Dispose();
                    onDone(app);
                };
            }
            catch (Exception)
            {
                // L'appli ne démarre pas : la tuile garde son état
            }
        }
    }

    /// <summary>Relit, en arrière-plan (schtasks est lent), quelles applis démarrent en administrateur.</summary>
    public static async Task RefreshAdminStateAsync(IEnumerable<NeoApp> apps)
    {
        foreach (var app in apps)
        {
            var installed = app.IsInstalled;
            app.RunsAsAdmin = installed && await Task.Run(() => Neo.Shared.ElevatedLaunch.IsEnabled(app.Id));
        }
    }

    public static void Launch(NeoApp app)
    {
        if (app.ExecutablePath is null)
            return;
        // Démarrage administrateur activé : lancement via la tâche planifiée, sans invite UAC.
        // (Si l'état n'est pas encore connu, l'appli fait elle-même ce détour au démarrage)
        if (app.RunsAsAdmin && Neo.Shared.ElevatedLaunch.TryLaunch(app.Id, ""))
            return;
        // Les applis Neo sont à instance unique : si elle tourne déjà, sa fenêtre est réaffichée
        Process.Start(new ProcessStartInfo(app.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath),
        });
    }

    private sealed record PackageInfo(string Path, string? Version, long Size, string? Sha256);

    /// <summary>
    /// Complète le catalogue local avec celui de la dernière release GitHub (catalog.json + &lt;App&gt;.zip) :
    /// une appli dont le paquet en ligne est plus récent est proposée en téléchargement (installation ou mise à jour).
    /// À appeler depuis le thread de l'interface (les tuiles se mettent à jour d'elles-mêmes). Sans connexion : rien ne change.
    /// </summary>
    public static async Task MergeOnlineCatalogAsync(IEnumerable<NeoApp> apps)
    {
        var release = await GitHubReleases.GetLatestAsync();
        if (release?.Asset("catalog.json") is not { } catalogAsset || await GitHubReleases.DownloadTextAsync(catalogAsset) is not { } json)
            return;

        try
        {
            // catalog.json est écrit avec une marque d'encodage (BOM) par build-packages.ps1
            using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
            foreach (var app in apps.Where(a => !a.IsBusy))
            {
                if (!doc.RootElement.TryGetProperty(app.Id, out var entry) || release.Asset($"{app.Id}.zip") is not { } zip)
                    continue;
                if (!Version.TryParse(entry.TryGetProperty("version", out var ver) ? ver.GetString() : null, out var online))
                    continue;
                // Le paquet local (dossier de développement ou installateur) suffit s'il est aussi récent
                if (app.AvailableVersion is { } local && online <= local)
                    continue;

                app.PackageSource = zip.Url;
                app.AvailableVersion = online;
                app.PackageSize = zip.Size;
                app.PackageSha256 = entry.TryGetProperty("sha256", out var sha) ? sha.GetString() : null;
                app.NotifyStateChanged();
            }
        }
        catch (JsonException)
        {
            // Catalogue en ligne illisible : on garde le catalogue local
        }
    }

    /// <summary>
    /// Catalogue des paquets (catalog.json, produit par tools\build-packages.ps1) :
    /// celui du dossier de développement s'il existe (à côté de NeoSuite.sln), sinon celui déposé par
    /// l'installateur dans Program Files\Neo\Packages.
    /// TODO : remplacer par un catalogue en ligne quand les paquets seront hébergés.
    /// </summary>
    public static string? CatalogPath
    {
        get
        {
            if (FindSuiteRoot() is { } root && File.Exists(Path.Combine(root, "packages", "catalog.json")))
                return Path.Combine(root, "packages", "catalog.json");
            var installed = Path.Combine(InstallRoot, "Packages", "catalog.json");
            return File.Exists(installed) ? installed : null;
        }
    }

    private static Dictionary<string, PackageInfo>? LoadPackageCatalog()
    {
        if (CatalogPath is not { } catalogPath)
            return null;
        var folder = Path.GetDirectoryName(catalogPath)!;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            var result = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var e = entry.Value;
                result[entry.Name] = new PackageInfo(
                    Path.Combine(folder, $"{entry.Name}.zip"),
                    e.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                    e.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    e.TryGetProperty("sha256", out var sha) ? sha.GetString() : null);
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Le dossier NeoSuite est celui qui contient NeoSuite.sln, au-dessus de l'exécutable.</summary>
    public static string? FindSuiteRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NeoSuite.sln")))
                return dir.FullName;
        }
        return null;
    }
}
