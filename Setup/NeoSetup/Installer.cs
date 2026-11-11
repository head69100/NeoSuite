using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using Neo.Shared;

namespace NeoSetup;

public sealed record InstallOptions(bool StartWithWindows, bool DesktopShortcut);

public sealed record UninstallOptions(bool RemoveApps, bool RemoveSettings);

/// <summary>
/// Installe NeoSuite dans Program Files\Neo\NeoSuite. Les applis ne sont pas dans l'installateur : NeoSuite (et
/// NeoSoft) les téléchargent depuis la dernière release GitHub quand l'utilisateur les installe.
/// Un ancien installateur pouvait apporter leurs paquets (Packages/) : ils sont encore acceptés.
/// </summary>
public static class Installer
{
    private const string UninstallKeyRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Toutes les applis de la suite (pour les fermer et les désinstaller).</summary>
    public static readonly string[] NeoApps = ["NeoSuite", "NeoDriver", "NeoUninstaller", "NeoBoost", "NeoSoft", "NeoRar", "NeoMedia", "NeoSound", "NeoTorrent"];

    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neo");
    public static string SuiteDir => Path.Combine(Root, "NeoSuite");
    public static string SuiteExe => Path.Combine(SuiteDir, "NeoSuite.exe");
    public static string PackagesDir => Path.Combine(Root, "Packages");
    public static string UninstallerExe => Path.Combine(Root, "NeoSuite-Uninstall.exe");

    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "NeoSuite.lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NeoSuite.lnk");

    public static bool IsInstalled => File.Exists(SuiteExe);

    /// <summary>Version de NeoSuite déjà installée (null s'il ne l'est pas).</summary>
    public static Version? InstalledVersion => IsInstalled ? InstallerPackage.ParseVersion(FileVersionInfo.GetVersionInfo(SuiteExe).ProductVersion) : null;

    /// <summary>Réglages de l'installation en place, à conserver lors d'une mise à jour.</summary>
    public static bool StartupEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("NeoSuite") is string;
        }
    }

    public static bool DesktopShortcutExists => File.Exists(DesktopShortcut);

    // ======================================================================
    // Installation
    // ======================================================================

    public static void Install(InstallerPackage payload, InstallOptions options, IProgress<(string Text, double Percent)> progress)
    {
        progress.Report(("Fermeture de NeoSuite…", 0));
        StopProcesses(["NeoSuite"]);
        Directory.CreateDirectory(Root);

        // Copie à côté puis remplacement : une installation interrompue ne casse pas celle en place
        var staging = SuiteDir + ".new";
        var packagesStaging = PackagesDir + ".new";
        DeleteDirectory(staging);
        DeleteDirectory(packagesStaging);

        var entries = payload.Archive.Entries.Where(e => e.Name.Length > 0).ToList();
        var hasPackages = entries.Any(e => e.FullName.StartsWith("Packages/"));
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        foreach (var entry in entries)
        {
            var (prefix, target) = entry.FullName.StartsWith("NeoSuite/") ? ("NeoSuite/", staging)
                : entry.FullName.StartsWith("Packages/") ? ("Packages/", packagesStaging)
                : (null, null);
            if (prefix is null || target is null)
                continue;

            var path = Path.GetFullPath(Path.Combine(target, entry.FullName[prefix.Length..]));
            if (!path.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                continue; // chemin hors du dossier : refusé
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            progress.Report((prefix == "Packages/" ? $"Copie de {Path.GetFileNameWithoutExtension(entry.Name)}…" : "Installation de NeoSuite…", 5 + 85.0 * done / total));
            entry.ExtractToFile(path, overwrite: true);
            done += entry.Length;
        }

        progress.Report(("Finalisation…", 92));
        DeleteDirectory(SuiteDir);
        Directory.Move(staging, SuiteDir);
        // Sans paquets dans l'installateur, ceux d'une ancienne version (plusieurs centaines de Mo) sont retirés :
        // NeoSuite propose les versions en ligne, plus récentes
        DeleteDirectory(PackagesDir);
        if (hasPackages)
            Directory.Move(packagesStaging, PackagesDir);

        // Désinstallateur : ce même programme, sans les fichiers à installer
        using (var self = File.OpenRead(Environment.ProcessPath!))
        using (var output = File.Create(UninstallerExe))
        {
            var buffer = new byte[1 << 20];
            var remaining = payload.StubLength;
            while (remaining > 0)
            {
                var read = self.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    break;
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        CreateShortcut(StartMenuShortcut, SuiteExe, "Vos applications Neo, réunies dans un seul panneau");
        if (options.DesktopShortcut)
            CreateShortcut(DesktopShortcut, SuiteExe, "Vos applications Neo, réunies dans un seul panneau");
        else if (File.Exists(DesktopShortcut))
            File.Delete(DesktopShortcut);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (options.StartWithWindows)
                run.SetValue("NeoSuite", $"\"{SuiteExe}\" --background");
            else
                run.DeleteValue("NeoSuite", throwOnMissingValue: false);
        }

        // Pour NeoSoft : où trouver NeoSuite et les paquets (NeoSuite le refait à chaque lancement)
        using (var neo = Registry.CurrentUser.CreateSubKey(@"Software\Neo"))
        {
            neo.SetValue("SuiteExe", SuiteExe);
            if (hasPackages)
                neo.SetValue("PackagesCatalog", Path.Combine(PackagesDir, "catalog.json"));
            else
                neo.DeleteValue("PackagesCatalog", throwOnMissingValue: false);
        }

        RegisterUninstallEntry();
        progress.Report(("NeoSuite est installé.", 100));
    }

    private static void RegisterUninstallEntry()
    {
        var size = new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.FullName.StartsWith(SuiteDir, StringComparison.OrdinalIgnoreCase) || f.FullName.StartsWith(PackagesDir, StringComparison.OrdinalIgnoreCase))
            .Sum(f => f.Length) / 1024;
        var version = FileVersionInfo.GetVersionInfo(SuiteExe).ProductVersion?.Split('+')[0] ?? "";

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey($@"{UninstallKeyRoot}\NeoSuite");
        key.SetValue("DisplayName", "NeoSuite");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Neo");
        key.SetValue("DisplayIcon", SuiteExe);
        key.SetValue("InstallLocation", SuiteDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(size, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"\"{UninstallerExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Lance NeoSuite sans les droits administrateur de l'installateur (via l'Explorateur).</summary>
    public static void LaunchSuite()
    {
        if (File.Exists(SuiteExe))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SuiteExe}\"") { UseShellExecute = true });
    }

    // ======================================================================
    // Désinstallation
    // ======================================================================

    public static void Uninstall(UninstallOptions options, IProgress<(string Text, double Percent)> progress)
    {
        progress.Report(("Fermeture des applications Neo…", 0));
        StopProcesses(NeoApps);

        if (options.RemoveApps && Directory.Exists(Root))
        {
            var apps = Directory.GetDirectories(Root)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n != "NeoSuite" && n != "Packages" && !n.EndsWith(".new"))
                .ToList();
            for (var i = 0; i < apps.Count; i++)
            {
                var id = apps[i]!;
                progress.Report(($"Désinstallation de {id}…", 5 + 70.0 * i / Math.Max(1, apps.Count)));
                UninstallApp(id, options.RemoveSettings);
            }
        }

        progress.Report(("Suppression de NeoSuite…", 80));
        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
        {
            if (File.Exists(shortcut))
                File.Delete(shortcut);
        }
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue("NeoSuite", throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Neo", throwOnMissingSubKey: false);
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            hklm.DeleteSubKeyTree($@"{UninstallKeyRoot}\NeoSuite", throwOnMissingSubKey: false);

        DeleteDirectory(SuiteDir);
        DeleteDirectory(PackagesDir);
        if (options.RemoveSettings)
            DeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoSuite"));

        progress.Report(("NeoSuite est désinstallé.", 100));
    }

    /// <summary>Même nettoyage que NeoSuite quand il désinstalle une appli (voir InstallWorker.Uninstall).</summary>
    private static void UninstallApp(string id, bool removeSettings)
    {
        var dir = Path.Combine(Root, id);
        var exe = Path.Combine(dir, $"{id}.exe");
        var hook = Path.Combine(dir, "neo-uninstall-hook");
        if (File.Exists(hook) && File.Exists(exe))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, File.ReadAllText(hook).Trim()) { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(15_000);
            }
            catch (Exception)
            {
            }
        }

        RunQuiet("schtasks.exe", $"/Delete /TN \"{id}\" /F");

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            using (var key = hklm.OpenSubKey($@"{UninstallKeyRoot}\Neo.{id}"))
            {
                if (key?.GetValue("DisplayName") is string name)
                {
                    var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), $"{name}.lnk");
                    if (File.Exists(shortcut))
                        File.Delete(shortcut);
                }
            }
            hklm.DeleteSubKeyTree($@"{UninstallKeyRoot}\Neo.{id}", throwOnMissingSubKey: false);
        }

        DeleteDirectory(dir);
        if (removeSettings)
            DeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id));
    }

    /// <summary>Le désinstallateur ne peut pas s'effacer lui-même pendant qu'il tourne : Windows s'en charge juste après.</summary>
    public static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath!;
        var command = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{self}\" & rmdir \"{Root}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    // ======================================================================
    // Utilitaires
    // ======================================================================

    private static void StopProcesses(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    // Seulement les copies installées (pas une version de développement ouverte ailleurs)
                    if (path is not null && path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    private static void CreateShortcut(string path, string target, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                Thread.Sleep(500); // un processus qui vient d'être fermé libère ses fichiers
            }
        }
    }

    private static void RunQuiet(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }
}
