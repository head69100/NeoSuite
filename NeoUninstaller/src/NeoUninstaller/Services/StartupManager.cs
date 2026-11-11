using System.Diagnostics;
using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Programmes lancés au démarrage : clés Run, dossiers Démarrage et tâches planifiées d'ouverture de session.
/// L'activation/désactivation passe par StartupApproved, comme le Gestionnaire des tâches de Windows :
/// l'entrée est conservée et Windows affiche le même état dans Paramètres › Applications › Démarrage.
/// </summary>
public static class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0, TaskEnumHidden = 1;

    public static Task<List<StartupItem>> LoadAsync() => Task.Run(Load);

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        ReadRun(RegistryHive.CurrentUser, RunPath, StartupSource.RegistryUser, "Run", items);
        ReadRun(RegistryHive.LocalMachine, RunPath, StartupSource.RegistryMachine, "Run", items);
        ReadRun(RegistryHive.LocalMachine, Run32Path, StartupSource.RegistryMachine32, "Run32", items);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser, RegistryHive.CurrentUser, items);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon, RegistryHive.LocalMachine, items);
        ReadTasks(items);
        return items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- Lecture ----------

    private static void ReadRun(RegistryHive hive, string path, StartupSource source, string approvedName, List<StartupItem> items)
    {
        using var key = LeftoverScanner.OpenKey(hive, path);
        if (key is null)
            return;
        foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
        {
            if (key.GetValue(name) is not string command)
                continue;
            items.Add(Create(source, name, command, IsApproved(hive, approvedName, name)));
        }
    }

    private static void ReadFolder(string folder, StartupSource source, RegistryHive hive, List<StartupItem> items)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) ?? file : file;
            items.Add(Create(source, file, target, IsApproved(hive, "StartupFolder", fileName)));
        }
    }

    private static void ReadTasks(List<StartupItem> items)
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            ReadTaskFolder(service.GetFolder("\\"), items);
        }
        catch (Exception)
        {
            // Planificateur indisponible : on se contente du registre et des dossiers
        }
    }

    private static void ReadTaskFolder(dynamic folder, List<StartupItem> items)
    {
        string folderPath = folder.Path;
        // Les tâches de Windows lui-même ne sont pas des « programmes au démarrage »
        if (folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
        {
            try
            {
                dynamic definition = task.Definition;
                var atStartup = false;
                foreach (dynamic trigger in definition.Triggers)
                {
                    if ((int)trigger.Type is TriggerLogon or TriggerBoot)
                        atStartup = true;
                }
                if (!atStartup)
                    continue;

                string? command = null;
                foreach (dynamic action in definition.Actions)
                {
                    if ((int)action.Type == ActionExec)
                    {
                        // Le chemin de la tâche est parfois déjà entre guillemets
                        command = $"\"{((string)action.Path).Trim('"')}\" {(string)(action.Arguments ?? "")}".Trim();
                        break;
                    }
                }
                if (command is null)
                    continue;

                string userId = definition.Principal.UserId ?? "";
                items.Add(new StartupItem
                {
                    Source = StartupSource.ScheduledTask,
                    Name = (string)task.Path,
                    Command = command,
                    ExecutablePath = ExeOf(command),
                    Publisher = string.IsNullOrWhiteSpace((string)definition.RegistrationInfo.Author) ? Info(ExeOf(command))?.CompanyName : (string)definition.RegistrationInfo.Author,
                    Description = Info(ExeOf(command))?.FileDescription is { Length: > 0 } d ? d : (string)task.Name,
                    IsUserTask = userId.EndsWith(Environment.UserName, StringComparison.OrdinalIgnoreCase),
                    IsEnabled = (bool)task.Enabled,
                });
            }
            catch (Exception)
            {
                // Tâche illisible (droits) : ignorée
            }
        }

        foreach (dynamic sub in folder.GetFolders(0))
            ReadTaskFolder(sub, items);
    }

    private static StartupItem Create(StartupSource source, string name, string command, bool enabled)
    {
        var exe = ExeOf(command);
        var info = Info(exe);
        return new StartupItem
        {
            Source = source,
            Name = name,
            Command = command,
            ExecutablePath = exe,
            Publisher = info?.CompanyName,
            Description = source is StartupSource.FolderUser or StartupSource.FolderCommon
                ? Path.GetFileNameWithoutExtension(name)
                : info?.FileDescription is { Length: > 0 } d ? d : name,
            IsEnabled = enabled,
        };
    }

    /// <summary>Valeur StartupApproved : absente ou premier octet pair = activé, impair = désactivé.</summary>
    private static bool IsApproved(RegistryHive hive, string approvedName, string valueName)
    {
        using var key = LeftoverScanner.OpenKey(hive, $@"{ApprovedPath}\{approvedName}");
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    // ---------- Modification ----------

    public static Task SetEnabledAsync(StartupItem item, bool enabled) => Task.Run(() =>
    {
        if (item.Source == StartupSource.ScheduledTask)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var slash = item.Name.LastIndexOf('\\');
            dynamic folder = service.GetFolder(slash <= 0 ? "\\" : item.Name[..slash]);
            dynamic task = folder.GetTask(item.Name[(slash + 1)..]);
            task.Enabled = enabled;
            return;
        }

        var (hive, approvedName, valueName) = item.Source switch
        {
            StartupSource.RegistryUser => (RegistryHive.CurrentUser, "Run", item.Name),
            StartupSource.RegistryMachine => (RegistryHive.LocalMachine, "Run", item.Name),
            StartupSource.RegistryMachine32 => (RegistryHive.LocalMachine, "Run32", item.Name),
            StartupSource.FolderUser => (RegistryHive.CurrentUser, "StartupFolder", Path.GetFileName(item.Name)),
            _ => (RegistryHive.LocalMachine, "StartupFolder", Path.GetFileName(item.Name)),
        };

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{ApprovedPath}\{approvedName}", writable: true);
        // Même format que le Gestionnaire des tâches : 02 = activé, 03 = désactivé + date (FILETIME)
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(valueName, data, RegistryValueKind.Binary);
    });

    /// <summary>Supprime définitivement l'entrée (registre sauvegardé, fichier dans la Corbeille, tâche exportée en XML).</summary>
    public static async Task<CleanReport?> DeleteAsync(StartupItem item)
    {
        switch (item.Source)
        {
            case StartupSource.ScheduledTask:
                await Task.Run(() => DeleteTask(item));
                return null;
            case StartupSource.FolderUser or StartupSource.FolderCommon:
                return await LeftoverCleaner.CleanAsync($"Démarrage - {item.DisplayName}",
                    [new Leftover { Kind = LeftoverKind.File, Path = item.Name, Reason = "Démarrage", Confidence = Confidence.Certain }]);
            default:
                var keyPath = item.Source switch
                {
                    StartupSource.RegistryUser => LeftoverScanner.FullPath(RegistryHive.CurrentUser, RunPath),
                    StartupSource.RegistryMachine => LeftoverScanner.FullPath(RegistryHive.LocalMachine, RunPath),
                    _ => LeftoverScanner.FullPath(RegistryHive.LocalMachine, Run32Path),
                };
                return await LeftoverCleaner.CleanAsync($"Démarrage - {item.DisplayName}",
                    [new Leftover { Kind = LeftoverKind.RegistryValue, Path = keyPath, ValueName = item.Name, Reason = "Démarrage", Confidence = Confidence.Certain }]);
        }
    }

    private static void DeleteTask(StartupItem item)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        var slash = item.Name.LastIndexOf('\\');
        dynamic folder = service.GetFolder(slash <= 0 ? "\\" : item.Name[..slash]);
        dynamic task = folder.GetTask(item.Name[(slash + 1)..]);

        // Sauvegarde XML : réimportable avec « schtasks /Create /XML »
        var backup = Path.Combine(LeftoverCleaner.BackupsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss} Tâche {SafeName(item.DisplayName)}");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "tache.xml"), (string)task.Xml, System.Text.Encoding.Unicode);

        folder.DeleteTask(item.Name[(slash + 1)..], 0);
    }

    // ---------- Utilitaires ----------

    private static string? ExeOf(string command)
    {
        var (path, _) = IconService.ParseIconLocation(command);
        if (path is null)
            return null;
        if (!Path.IsPathRooted(path))
        {
            // « rundll32 … », « cmd /c … » : on cherche dans le dossier système
            var system = Path.Combine(Environment.SystemDirectory, path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : path + ".exe");
            return File.Exists(system) ? system : null;
        }
        return File.Exists(path) ? path : null;
    }

    private static FileVersionInfo? Info(string? exe)
    {
        try
        {
            return exe is null ? null : FileVersionInfo.GetVersionInfo(exe);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveShortcut(string link)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(link);
            string target = shortcut.TargetPath;
            string args = shortcut.Arguments;
            return string.IsNullOrWhiteSpace(target) ? null : $"\"{target}\" {args}".Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
