using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using Microsoft.Win32;

namespace NeoSuite.Services;

/// <summary>
/// Partie « administrateur » de l'installation : NeoSuite se relance lui-même en mode sans fenêtre
/// (--install-worker / --uninstall-worker) avec les droits admin pour écrire dans Program Files,
/// le menu Démarrer commun et la liste « Applications installées » de Windows.
/// </summary>
public static class InstallWorker
{
    public const string InstallArg = "--install-worker";
    public const string UninstallArg = "--uninstall-worker";
    public const string RemoveDataArg = "--remove-data";
    public const string AdminArg = "--admin-worker";

    private const string UninstallKeyRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Traite les arguments de ligne de commande ; renvoie null si ce n'est pas un appel « worker ».</summary>
    public static int? TryRun(string[] args)
    {
        var mode = args.FirstOrDefault(a => a is InstallArg or UninstallArg or AdminArg);
        if (mode is null)
            return null;

        var rest = args.SkipWhile(a => a != mode).Skip(1).ToArray();

        // Lancé depuis « Applications installées » de Windows sans droits : on se relance en admin
        if (!IsAdministrator())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args.Skip(1).Select(Quote)))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
                return p?.ExitCode ?? 1;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return 1223; // UAC refusé
            }
        }

        string? resultFile = null;
        try
        {
            if (mode == InstallArg)
            {
                // --install-worker <id> <nom> <zip> <fichier résultat>
                var (id, name, zip) = (rest[0], rest[1], rest[2]);
                resultFile = rest.ElementAtOrDefault(3);
                Install(id, name, zip);
            }
            else if (mode == AdminArg)
            {
                // --admin-worker <id> on|off <fichier résultat>
                var (id, enable) = (rest[0], rest[1] == "on");
                resultFile = rest.ElementAtOrDefault(2);
                if (enable)
                    Neo.Shared.ElevatedLaunch.Register(id);
                else
                    Neo.Shared.ElevatedLaunch.Unregister(id);
            }
            else
            {
                // --uninstall-worker <id> [<fichier résultat>] [--remove-data]
                var id = rest[0];
                resultFile = rest.Skip(1).FirstOrDefault(a => a != RemoveDataArg);
                Uninstall(id, removeData: rest.Contains(RemoveDataArg));
            }
            WriteResult(resultFile, "OK");
            return 0;
        }
        catch (Exception ex)
        {
            WriteResult(resultFile, "ERR:" + ex.Message);
            return 1;
        }
    }

    private static void Install(string id, string name, string zipPath)
    {
        var dir = AppCatalog.InstallDir(id);
        var staging = dir + ".new";

        // Extraction à côté, puis remplacement : une installation ratée ne casse pas la version en place
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        var exe = Path.Combine(staging, $"{id}.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException($"Le paquet ne contient pas {id}.exe.");

        StopRunningInstances(id, dir);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.Move(staging, dir);

        // Empreinte du paquet installé : NeoSuite propose la mise à jour dès que le paquet change,
        // même si le numéro de version est resté le même
        using (var zip = File.OpenRead(zipPath))
            File.WriteAllText(Path.Combine(dir, AppCatalog.PackageHashFile), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)));

        var installedExe = Path.Combine(dir, $"{id}.exe");
        CreateStartMenuShortcut(name, installedExe);
        RegisterUninstallEntry(id, name, installedExe, dir);
        RunHook(id, dir, "neo-install-hook");
    }

    private static void Uninstall(string id, bool removeData)
    {
        var dir = AppCatalog.InstallDir(id);
        StopRunningInstances(id, dir);
        RunHook(id, dir, "neo-uninstall-hook");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        var name = ReadDisplayName(id) ?? id;
        var shortcut = ShortcutPath(name);
        if (File.Exists(shortcut))
            File.Delete(shortcut);

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            hklm.DeleteSubKeyTree($@"{UninstallKeyRoot}\Neo.{id}", throwOnMissingSubKey: false);

        // Tâche planifiée éventuelle de l'appli (ex. démarrage admin de NeoDriver)
        RunQuiet("schtasks.exe", $"/Delete /TN \"{id}\" /F");

        if (removeData)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id);
            if (Directory.Exists(data))
                Directory.Delete(data, recursive: true);
        }

        if (Directory.Exists(AppCatalog.InstallRoot) && !Directory.EnumerateFileSystemEntries(AppCatalog.InstallRoot).Any())
            Directory.Delete(AppCatalog.InstallRoot);
    }

    /// <summary>Ferme l'appli si elle tourne depuis le dossier d'installation (sinon ses fichiers sont verrouillés).</summary>
    private static void StopRunningInstances(string id, string dir)
    {
        foreach (var p in Process.GetProcessesByName(id))
        {
            try
            {
                if (p.MainModule?.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Processus déjà terminé ou inaccessible : on continue
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Étape propre à l'appli après installation (« neo-install-hook ») ou avant suppression (« neo-uninstall-hook »),
    /// ex. NeoRar qui active ou retire son intégration à l'Explorateur : l'appli est lancée avec les arguments du fichier.
    /// </summary>
    private static void RunHook(string id, string dir, string hookFile)
    {
        var hook = Path.Combine(dir, hookFile);
        var exe = Path.Combine(dir, $"{id}.exe");
        if (!File.Exists(hook) || !File.Exists(exe))
            return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, File.ReadAllText(hook).Trim()) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15_000);
        }
        catch (Exception)
        {
            // Le nettoyage est un plus : la désinstallation continue
        }
    }

    private static string ShortcutPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), $"{name}.lnk");

    private static void CreateStartMenuShortcut(string name, string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath(name));
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.IconLocation = exe + ",0";
        link.Description = $"{name} · suite Neo";
        link.Save();
    }

    private static void RegisterUninstallEntry(string id, string name, string exe, string dir)
    {
        var sizeKb = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? "";

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey($@"{UninstallKeyRoot}\Neo.{id}");
        key.SetValue("DisplayName", name);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Neo");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", dir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" {UninstallArg} {id}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string? ReadDisplayName(string id)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"{UninstallKeyRoot}\Neo.{id}");
        return key?.GetValue("DisplayName") as string;
    }

    private static void WriteResult(string? file, string content)
    {
        if (string.IsNullOrEmpty(file))
            return;
        try
        {
            File.WriteAllText(file, content);
        }
        catch (IOException)
        {
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

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
