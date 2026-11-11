using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Nettoyeur de registre prudent : uniquement des entrées qui pointent vers un fichier qui n'existe plus
/// (programmes désinstallés à la main, raccourcis cassés…). Tout est sauvegardé avant suppression
/// via <see cref="LeftoverCleaner"/>, et restaurable depuis l'Historique.
/// </summary>
public static class RegistryIssueScanner
{
    public static Task<List<Leftover>> ScanAsync(bool isAdmin) => Task.Run(() =>
    {
        var issues = new List<Leftover>();
        ScanUninstallEntries(issues);
        ScanAppPaths(issues);
        ScanRun(issues);
        ScanMuiCache(issues);
        ScanSharedDlls(issues);
        ScanOpenWith(issues);
        ScanBrokenShortcuts(issues);
        foreach (var issue in issues)
        {
            issue.CanDelete = !issue.RequiresAdmin || isAdmin;
            issue.IsChecked = issue.CanDelete;
        }
        return issues;
    });

    // Entrées « Applications installées » dont le désinstalleur ET le dossier ont disparu
    private static void ScanUninstallEntries(List<Leftover> issues)
    {
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            using var root = LeftoverScanner.OpenKey(hive, path);
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string display || key.GetValue("UninstallString") is not string uninstall)
                    continue;
                if (uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || key.GetValue("SystemComponent") is 1)
                    continue; // MSI : géré par Windows Installer
                var exe = Exe(uninstall);
                var location = (key.GetValue("InstallLocation") as string)?.Trim('"');
                if (exe is null || File.Exists(exe) || (!string.IsNullOrEmpty(location) && Directory.Exists(location)))
                    continue;
                issues.Add(Issue(LeftoverKind.RegistryKey, LeftoverScanner.FullPath(hive, $@"{path}\{name}"), null,
                    $"« {display} » n'est plus installé (désinstalleur introuvable)", hive));
            }
        }
    }

    private static void ScanAppPaths(List<Leftover> issues)
    {
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
        })
        {
            using var root = LeftoverScanner.OpenKey(hive, path);
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue(null) is string target && Exe(target) is { } exe && !File.Exists(exe))
                    issues.Add(Issue(LeftoverKind.RegistryKey, LeftoverScanner.FullPath(hive, $@"{path}\{name}"), null, "Chemin d'application vers un fichier disparu", hive));
            }
        }
    }

    private static void ScanRun(List<Leftover> issues)
    {
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        })
        {
            using var key = LeftoverScanner.OpenKey(hive, path);
            if (key is null)
                continue;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                if (key.GetValue(name) is string command && Exe(command) is { } exe && !File.Exists(exe))
                    issues.Add(Issue(LeftoverKind.RegistryValue, LeftoverScanner.FullPath(hive, path), name, "Démarrage d'un programme disparu", hive));
            }
        }
    }

    // Cache des noms d'applis : « C:\…\app.exe.FriendlyAppName »
    private static void ScanMuiCache(List<Leftover> issues)
    {
        const string path = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        using var key = LeftoverScanner.OpenKey(RegistryHive.CurrentUser, path);
        if (key is null)
            return;
        foreach (var name in key.GetValueNames())
        {
            var dot = name.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (dot <= 2 || name[1] != ':')
                continue; // seulement les chemins locaux « X:\… »
            var exe = name[..(dot + 4)];
            if (!File.Exists(exe) && Directory.Exists(Path.GetPathRoot(exe)))
                issues.Add(Issue(LeftoverKind.RegistryValue, LeftoverScanner.FullPath(RegistryHive.CurrentUser, path), name, "Nom en cache d'une appli disparue", RegistryHive.CurrentUser));
        }
    }

    private static void ScanSharedDlls(List<Leftover> issues)
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" })
        {
            using var key = LeftoverScanner.OpenKey(RegistryHive.LocalMachine, path);
            if (key is null)
                continue;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length > 3 && name[1] == ':' && !File.Exists(name) && Directory.Exists(Path.GetPathRoot(name)))
                    issues.Add(Issue(LeftoverKind.RegistryValue, LeftoverScanner.FullPath(RegistryHive.LocalMachine, path), name, "DLL partagée disparue", RegistryHive.LocalMachine));
            }
        }
    }

    // « Ouvrir avec » vers un exe disparu
    private static void ScanOpenWith(List<Leftover> issues)
    {
        foreach (var (hive, path) in new[] { (RegistryHive.CurrentUser, @"Software\Classes\Applications"), (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Applications") })
        {
            using var root = LeftoverScanner.OpenKey(hive, path);
            if (root is null)
                continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var command = root.OpenSubKey($@"{name}\shell\open\command");
                if (command?.GetValue(null) is string cmd && Exe(Environment.ExpandEnvironmentVariables(cmd)) is { } exe
                    && Path.IsPathRooted(exe) && !File.Exists(exe) && Directory.Exists(Path.GetPathRoot(exe)))
                    issues.Add(Issue(LeftoverKind.RegistryKey, LeftoverScanner.FullPath(hive, $@"{path}\{name}"), null, "« Ouvrir avec » vers un programme disparu", hive));
            }
        }
    }

    // Raccourcis du menu Démarrer et du Bureau qui ne mènent plus nulle part
    private static void ScanBrokenShortcuts(List<Leftover> issues)
    {
        dynamic shell;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        }
        catch (Exception)
        {
            return;
        }

        foreach (var (folder, recursive, admin) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true, true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false, true),
        })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }))
            {
                try
                {
                    string target = shell.CreateShortcut(link).TargetPath;
                    // Seulement les cibles locales clairement absentes (pas les lecteurs réseau ou débranchés)
                    if (string.IsNullOrWhiteSpace(target) || target.StartsWith(@"\\") || !Path.IsPathRooted(target)
                        || !Directory.Exists(Path.GetPathRoot(target)) || File.Exists(target) || Directory.Exists(target))
                        continue;
                    issues.Add(new Leftover
                    {
                        Kind = LeftoverKind.Shortcut,
                        Path = link,
                        Reason = $"Raccourci cassé (cible absente : {target})",
                        Confidence = Confidence.Certain,
                        RequiresAdmin = admin,
                    });
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static Leftover Issue(LeftoverKind kind, string path, string? valueName, string reason, RegistryHive hive) => new()
    {
        Kind = kind,
        Path = path,
        ValueName = valueName,
        Reason = reason,
        Confidence = Confidence.Certain,
        RequiresAdmin = hive == RegistryHive.LocalMachine,
    };

    /// <summary>Exécutable d'une commande, seulement si c'est un chemin complet (sinon on ne peut rien affirmer).</summary>
    private static string? Exe(string command)
    {
        var (path, _) = IconService.ParseIconLocation(Environment.ExpandEnvironmentVariables(command));
        // Lecteur absent (disque externe débranché) : on ne peut pas dire que le fichier a disparu
        return path is not null && Path.IsPathRooted(path) && path.Length > 3 && path[1] == ':' && Directory.Exists(Path.GetPathRoot(path)) ? path : null;
    }
}
