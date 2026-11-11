using System.Diagnostics;
using System.Runtime.InteropServices;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Nettoyeur de fichiers inutiles, façon CCleaner : fichiers temporaires, caches de Windows, des navigateurs
/// et des applications, rapports d'erreurs, Corbeille. Les fichiers en cours d'utilisation sont ignorés.
/// </summary>
public static class JunkCleaner
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Catégories disponibles sur ce PC (les applis absentes ne sont pas proposées).</summary>
    public static List<JunkCategory> GetCategories(bool isAdmin)
    {
        var list = new List<JunkCategory>
        {
            new()
            {
                Group = "Windows", Name = "Fichiers temporaires", Glyph = "\uE74D", IsChecked = true,
                Description = "Dossier Temp de votre compte (fichiers de plus de 24 h)",
                Locations = [new(Path.GetTempPath(), MinAgeHours: 24)],
            },
            new()
            {
                Group = "Windows", Name = "Fichiers temporaires de Windows", Glyph = "\uE74D", RequiresAdmin = true, IsChecked = true,
                Description = @"C:\Windows\Temp (fichiers de plus de 24 h)",
                Locations = [new(Path.Combine(WindowsDir, "Temp"), MinAgeHours: 24)],
            },
            new()
            {
                Group = "Windows", Name = "Cache de Windows Update", Glyph = "\uE895", RequiresAdmin = true, IsChecked = true,
                Description = "Mises à jour déjà installées, téléchargées par Windows Update",
                Locations = [new(Path.Combine(WindowsDir, @"SoftwareDistribution\Download"), MinAgeHours: 72)],
            },
            new()
            {
                Group = "Windows", Name = "Optimisation de la distribution", Glyph = "\uE896", RequiresAdmin = true, IsChecked = true,
                Description = "Morceaux de mises à jour partagés avec d'autres PC",
                Locations = [new(Path.Combine(WindowsDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))],
            },
            new()
            {
                Group = "Windows", Name = "Rapports d'erreurs Windows", Glyph = "\uE7BA", IsChecked = true,
                Description = "Rapports de plantage déjà envoyés ou en attente",
                Locations =
                [
                    new(Path.Combine(Local, @"Microsoft\Windows\WER")),
                    new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive")),
                    new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                ],
            },
            new()
            {
                Group = "Windows", Name = "Fichiers de vidage mémoire", Glyph = "\uE7BA", IsChecked = true,
                Description = "Copies de la mémoire après un plantage (utiles seulement pour un diagnostic)",
                Locations =
                [
                    new(Path.Combine(Local, "CrashDumps")),
                    new(Path.Combine(WindowsDir, "Minidump")),
                    new(WindowsDir, "MEMORY.DMP", Recursive: false),
                ],
            },
            new()
            {
                Group = "Windows", Name = "Cache des shaders DirectX", Glyph = "\uE7F4", IsChecked = true,
                Description = "Recréé automatiquement par les jeux et applis 3D",
                Locations = [new(Path.Combine(Local, "D3DSCache"))],
            },
            new()
            {
                Group = "Windows", Name = "Cache des miniatures", Glyph = "\uE91B",
                Description = "Aperçus des images dans l'Explorateur, recréés au besoin",
                Warning = "en partie verrouillé tant que l'Explorateur est ouvert",
                Locations = [new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false)],
            },
            new()
            {
                Group = "Confidentialité", Name = "Éléments récents", Glyph = "\uE81C",
                Description = "Liste des fichiers ouverts récemment (raccourcis uniquement, pas les fichiers)",
                Locations = [new(Path.Combine(Roaming, @"Microsoft\Windows\Recent"), "*.lnk", Recursive: false)],
            },
            new()
            {
                Group = "Windows", Name = "Corbeille", Glyph = "\uE74D", IsRecycleBin = true,
                Description = "Vide la Corbeille de tous les lecteurs",
                Warning = "les fichiers seront supprimés définitivement",
            },
        };

        AddChromiumBrowser(list, "Google Chrome", Path.Combine(Local, @"Google\Chrome\User Data"), "chrome");
        AddChromiumBrowser(list, "Microsoft Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"), "msedge");
        AddChromiumBrowser(list, "Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), "brave");
        AddChromiumBrowser(list, "Opera", Path.Combine(Roaming, @"Opera Software\Opera Stable"), "opera");
        AddChromiumBrowser(list, "Vivaldi", Path.Combine(Local, @"Vivaldi\User Data"), "vivaldi");

        var firefoxProfiles = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            list.Add(new JunkCategory
            {
                Group = "Navigateurs", Name = "Firefox · cache", Glyph = "\uE774", IsChecked = true, LockingProcess = "firefox",
                Description = "Pages et images mises en cache (historique et mots de passe conservés)",
                Locations = SafeDirs(firefoxProfiles).Select(p => new JunkLocation(Path.Combine(p, "cache2"))).ToList(),
            });
        }

        AddApp(list, "Discord · cache", "discord", Path.Combine(Roaming, "discord"), "Cache", "Code Cache", "GPUCache");
        AddApp(list, "Steam · cache web", "steamwebhelper", Path.Combine(Local, "Steam"), "htmlcache");
        AddApp(list, "Spotify · cache", "spotify", Path.Combine(Local, "Spotify"), "Data");
        AddApp(list, "AMD · cache des shaders", null, Path.Combine(Local, "AMD"), "DxCache", "DxcCache", "GLCache", "VkCache");
        AddApp(list, "NVIDIA · cache des shaders", null, Path.Combine(Local, "NVIDIA"), "DXCache", "GLCache");

        foreach (var c in list.Where(c => c.RequiresAdmin && !isAdmin))
        {
            c.CanClean = false;
            c.IsChecked = false;
            c.Status = "Droits administrateur requis";
        }
        return list.Where(c => c.IsRecycleBin || c.Locations.Any(l => Directory.Exists(l.Folder))).ToList();
    }

    private static void AddChromiumBrowser(List<JunkCategory> list, string name, string userData, string process)
    {
        if (!Directory.Exists(userData))
            return;
        var profiles = SafeDirs(userData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")).ToList();
        if (Directory.Exists(Path.Combine(userData, "Cache")))
            profiles.Add(userData); // Opera : profil directement dans le dossier

        var locations = new List<JunkLocation>();
        foreach (var profile in profiles)
        {
            locations.Add(new(Path.Combine(profile, @"Cache\Cache_Data")));
            locations.Add(new(Path.Combine(profile, "Code Cache")));
            locations.Add(new(Path.Combine(profile, "GPUCache")));
            locations.Add(new(Path.Combine(profile, @"Service Worker\CacheStorage")));
        }
        locations.Add(new(Path.Combine(userData, "ShaderCache")));
        locations.Add(new(Path.Combine(userData, "GrShaderCache")));

        list.Add(new JunkCategory
        {
            Group = "Navigateurs", Name = $"{name} · cache", Glyph = "\uE774", IsChecked = true, LockingProcess = process,
            Description = "Pages et images mises en cache (historique, cookies et mots de passe conservés)",
            Locations = locations,
        });
    }

    private static void AddApp(List<JunkCategory> list, string name, string? process, string root, params string[] subFolders)
    {
        if (!Directory.Exists(root))
            return;
        list.Add(new JunkCategory
        {
            Group = "Applications", Name = name, Glyph = "\uE71D", IsChecked = true, LockingProcess = process,
            Description = "Recréé automatiquement au besoin",
            Locations = subFolders.Select(s => new JunkLocation(Path.Combine(root, s))).ToList(),
        });
    }

    // ---------- Analyse ----------

    /// <summary>Taille et nombre de fichiers à supprimer (à appliquer à la catégorie sur le thread UI).</summary>
    public static Task<(long Size, int Count)> AnalyzeAsync(JunkCategory category) => Task.Run(() =>
    {
        if (category.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            SHQueryRecycleBin(null, ref info);
            return (info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue));
        }

        long size = 0;
        var count = 0;
        foreach (var file in EnumerateFiles(category))
        {
            size += file.Length;
            count++;
        }
        return (size, count);
    });

    // ---------- Nettoyage ----------

    public sealed record CleanResult(long FreedBytes, int Deleted, int Skipped);

    public static Task<CleanResult> CleanAsync(JunkCategory category) => Task.Run(() =>
    {
        if (category.IsRecycleBin)
        {
            var before = category.SizeBytes;
            SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // sans confirmation, sans barre, sans son
            return new CleanResult(Math.Max(before, 0), category.FileCount, 0);
        }

        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var file in EnumerateFiles(category).ToList())
        {
            try
            {
                var length = file.Length;
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                freed += length;
                deleted++;
            }
            catch (Exception)
            {
                skipped++; // fichier en cours d'utilisation ou protégé
            }
        }

        // Sous-dossiers vidés (jamais le dossier racine de la catégorie)
        foreach (var location in category.Locations.Where(l => l.Recursive && Directory.Exists(l.Folder)))
        {
            foreach (var dir in SafeAllDirs(location.Folder).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception)
                {
                }
            }
        }
        return new CleanResult(freed, deleted, skipped);
    });

    /// <summary>Le programme qui verrouille ces fichiers est-il ouvert ?</summary>
    public static bool IsLockedByRunningApp(JunkCategory category) =>
        category.LockingProcess is not null && Process.GetProcessesByName(category.LockingProcess).Length > 0;

    private static IEnumerable<FileInfo> EnumerateFiles(JunkCategory category)
    {
        foreach (var location in category.Locations)
        {
            if (!Directory.Exists(location.Folder))
                continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(location.Folder).EnumerateFiles(location.Pattern,
                    new EnumerationOptions { RecurseSubdirectories = location.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System });
            }
            catch (Exception)
            {
                continue;
            }

            var limit = DateTime.Now.AddHours(-location.MinAgeHours);
            foreach (var file in files)
            {
                bool keep;
                try
                {
                    keep = location.MinAgeHours == 0 || file.LastWriteTime < limit;
                }
                catch (Exception)
                {
                    keep = false;
                }
                if (keep)
                    yield return file;
            }
        }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeAllDirs(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
