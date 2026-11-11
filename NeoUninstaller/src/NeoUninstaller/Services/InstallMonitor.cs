using System.Text.Json;
using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

public sealed record MonitorItem(LeftoverKind Kind, string Path, string? ValueName);

public sealed record MonitorRecord(string Name, DateTime Date, List<string> ProgramNames, List<MonitorItem> Items);

/// <summary>Photographie de l'état du PC (dossiers, raccourcis, registre, services, tâches) à un instant donné.</summary>
public sealed class Snapshot
{
    public HashSet<string> Folders { get; set; } = [];
    public HashSet<string> Files { get; set; } = [];
    public HashSet<string> Keys { get; set; } = [];
    public HashSet<string> RunValues { get; set; } = [];     // « clé|valeur »
    public HashSet<string> Services { get; set; } = [];
    public HashSet<string> Tasks { get; set; } = [];
    public Dictionary<string, string> Programs { get; set; } = [];   // clé Uninstall → nom affiché
}

/// <summary>
/// Moniteur d'installation : cliché avant, installation du logiciel, cliché après. La différence liste tout ce que
/// l'installation a créé, ce qui permet de tout retrouver à la désinstallation.
/// </summary>
public static class InstallMonitor
{
    private static readonly string Folder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoUninstaller", "Monitor");
    private static string PendingFile => System.IO.Path.Combine(Folder, "avant.json");

    // Dossiers de bruit (créés en permanence par Windows) qu'on n'attribue jamais à une installation
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Microsoft", "Packages", "Windows", "CrashDumps", "D3DSCache", "INetCache", "History", "Comms", "ConnectedDevices",
        "PeerDistRepub", "Publishers", "VirtualStore", "Diagnostics", "Package Cache", "regid.1991-06.com.microsoft", "USOShared", "USOPrivate", "SoftwareDistribution",
    };

    public static bool HasPendingSnapshot => File.Exists(PendingFile);

    public static async Task StartAsync()
    {
        var snapshot = await Task.Run(Take);
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(PendingFile, JsonSerializer.Serialize(snapshot));
    }

    public static void Cancel()
    {
        if (File.Exists(PendingFile))
            File.Delete(PendingFile);
    }

    /// <summary>Cliché après, comparaison avec le cliché avant, enregistrement du résultat.</summary>
    public static async Task<MonitorRecord> FinishAsync(string name)
    {
        var before = JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(PendingFile)) ?? throw new InvalidOperationException("Cliché avant illisible.");
        var after = await Task.Run(Take);
        var record = Diff(name, before, after);

        // Nom laissé vide : on se rabat sur les programmes que Windows vient de déclarer
        if (string.IsNullOrWhiteSpace(record.Name))
            record = record with { Name = record.ProgramNames.FirstOrDefault() ?? $"Installation du {record.Date:dd/MM/yyyy}" };

        Directory.CreateDirectory(Folder);
        var file = System.IO.Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {Safe(record.Name)}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        Cancel();
        return record;
    }

    public static List<(string File, MonitorRecord Record)> LoadAll()
    {
        var list = new List<(string, MonitorRecord)>();
        if (!Directory.Exists(Folder))
            return list;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Where(f => !f.EndsWith("avant.json")))
        {
            try
            {
                if (JsonSerializer.Deserialize<MonitorRecord>(File.ReadAllText(file)) is { } record)
                    list.Add((file, record));
            }
            catch (JsonException)
            {
            }
        }
        return list.OrderByDescending(x => x.Item2.Date).ToList();
    }

    // ---------- Cliché ----------

    private static Snapshot Take()
    {
        var s = new Snapshot();

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        foreach (var root in roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var d1 in Dirs(root))
                s.Folders.Add(d1);
        }

        foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
        {
            foreach (var d in Dirs(menu))
                s.Folders.Add(d);
            foreach (var f in Files(menu, "*.lnk", recursive: true))
                s.Files.Add(f);
        }
        foreach (var desktop in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            foreach (var f in Files(desktop, "*.lnk", recursive: false))
                s.Files.Add(f);

        // Registre : deux niveaux sous SOFTWARE
        string[] softwareRoots = ["HKEY_LOCAL_MACHINE\\SOFTWARE", "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node", "HKEY_CURRENT_USER\\SOFTWARE"];
        foreach (var path in softwareRoots)
        {
            using var key = Open(path);
            if (key is null)
                continue;
            foreach (var name in SubKeys(key).Where(n => !Noise.Contains(n) && n is not ("Classes" or "Policies" or "Wow6432Node" or "Clients" or "RegisteredApplications")))
            {
                var full = path + "\\" + name;
                s.Keys.Add(full);
                using var sub = key.OpenSubKey(name);
                if (sub is null)
                    continue;
                foreach (var child in SubKeys(sub))
                    s.Keys.Add(full + "\\" + child);
            }
        }

        // Désinstalleurs déclarés
        foreach (var path in new[]
        {
            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
            "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
            "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        })
        {
            using var key = Open(path);
            if (key is null)
                continue;
            foreach (var name in SubKeys(key))
            {
                using var sub = key.OpenSubKey(name);
                var display = sub?.GetValue("DisplayName") as string;
                s.Keys.Add(path + "\\" + name);
                if (!string.IsNullOrWhiteSpace(display))
                    s.Programs[path + "\\" + name] = display.Trim();
            }
        }

        foreach (var path in new[]
        {
            "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",
            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",
            "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run",
        })
        {
            using var key = Open(path);
            if (key is null)
                continue;
            foreach (var value in key.GetValueNames().Where(v => v.Length > 0))
                s.RunValues.Add(path + "|" + value);
        }

        using (var services = Open("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services"))
        {
            if (services is not null)
                foreach (var name in SubKeys(services))
                    s.Services.Add(name);
        }

        try
        {
            s.Tasks = ScheduledTasks.Names();
        }
        catch (Exception)
        {
        }
        return s;
    }

    // ---------- Différence ----------

    private static MonitorRecord Diff(string name, Snapshot before, Snapshot after)
    {
        var items = new List<MonitorItem>();

        var newFolders = after.Folders.Except(before.Folders, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in Topmost(newFolders, '\\'))
            items.Add(new MonitorItem(LeftoverKind.Folder, f, null));

        foreach (var f in after.Files.Except(before.Files, StringComparer.OrdinalIgnoreCase))
            items.Add(new MonitorItem(LeftoverKind.Shortcut, f, null));

        var newKeys = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var k in Topmost(newKeys, '\\'))
            items.Add(new MonitorItem(LeftoverKind.RegistryKey, k, null));

        foreach (var v in after.RunValues.Except(before.RunValues, StringComparer.OrdinalIgnoreCase))
        {
            var bar = v.IndexOf('|');
            items.Add(new MonitorItem(LeftoverKind.RegistryValue, v[..bar], v[(bar + 1)..]));
        }
        foreach (var s in after.Services.Except(before.Services, StringComparer.OrdinalIgnoreCase))
            items.Add(new MonitorItem(LeftoverKind.Service, s, s));
        foreach (var t in after.Tasks.Except(before.Tasks, StringComparer.OrdinalIgnoreCase))
            items.Add(new MonitorItem(LeftoverKind.ScheduledTask, t, t));

        var programNames = after.Programs.Where(p => !before.Programs.ContainsKey(p.Key)).Select(p => p.Value).Distinct().ToList();
        return new MonitorRecord(name, DateTime.Now, programNames, items);
    }

    /// <summary>Ne garde que les chemins dont le parent n'est pas lui-même nouveau.</summary>
    private static IEnumerable<string> Topmost(List<string> paths, char separator)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var cut = p.LastIndexOf(separator);
            if (cut <= 0 || !set.Contains(p[..cut]))
                yield return p;
        }
    }

    // ---------- Utilitaires ----------

    private static IEnumerable<string> Dirs(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).Where(d => !Noise.Contains(System.IO.Path.GetFileName(d))).ToList()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> Files(string path, string pattern, bool recursive)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, pattern, new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }).ToList()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> SubKeys(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static RegistryKey? Open(string fullPath)
    {
        try
        {
            var slash = fullPath.IndexOf('\\');
            var hive = fullPath[..slash] == "HKEY_CURRENT_USER" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(fullPath[(slash + 1)..]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    /// <summary>
    /// Restes enregistrés par le moniteur pour un programme : ajoutés (ou confirmés) dans les résultats de l'analyse.
    /// </summary>
    public static List<Leftover> ForProgram(InstalledProgram program, bool isAdmin)
    {
        var result = new List<Leftover>();
        var tasks = (HashSet<string>?)null;
        var installDir = LeftoverScanner.ResolveInstallDir(program);
        foreach (var (_, record) in LoadAll())
        {
            var matches = record.ProgramNames.Any(n => n.Equals(program.DisplayName, StringComparison.OrdinalIgnoreCase))
                          || record.Name.Equals(program.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;

            foreach (var item in record.Items)
            {
                if (!StillThere(item, ref tasks))
                    continue;
                var admin = item.Kind is LeftoverKind.Service or LeftoverKind.ScheduledTask
                            || item.Path.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                            || !item.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) && item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut;
                var certain = IsClearlyOurs(item, program, installDir);
                var leftover = new Leftover
                {
                    Kind = item.Kind,
                    Path = item.Path,
                    ValueName = item.ValueName,
                    Reason = "Créé pendant l'installation (moniteur)",
                    Confidence = certain ? Confidence.Certain : Confidence.Probable,
                    RequiresAdmin = admin,
                };
                leftover.CanDelete = !admin || isAdmin;
                leftover.IsChecked = certain && leftover.CanDelete;
                result.Add(leftover);
            }
        }
        return result;
    }

    private static bool IsClearlyOurs(MonitorItem item, InstalledProgram program, string? installDir)
    {
        if (installDir is not null && item.Path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            return true;
        if (item.Path.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase))
            return true;
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        var path = Norm(item.Path + item.ValueName);
        var words = new[] { program.DisplayName.Split(' ', '-')[0], program.Publisher?.Split(' ', ',')[0] ?? "" }
            .Select(Norm).Where(w => w.Length >= 4);
        return words.Any(w => path.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StillThere(MonitorItem item, ref HashSet<string>? tasks)
    {
        switch (item.Kind)
        {
            case LeftoverKind.Folder:
                return Directory.Exists(item.Path);
            case LeftoverKind.File:
            case LeftoverKind.Shortcut:
                return File.Exists(item.Path);
            case LeftoverKind.RegistryKey:
                using (var key = Open(item.Path))
                    return key is not null;
            case LeftoverKind.RegistryValue:
                using (var key = Open(item.Path))
                    return key?.GetValue(item.ValueName!) is not null;
            case LeftoverKind.Service:
                using (var key = Open(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + item.ValueName))
                    return key is not null;
            case LeftoverKind.ScheduledTask:
                tasks ??= ScheduledTasks.Names();
                return tasks.Contains(item.ValueName!);
        }
        return false;
    }
}