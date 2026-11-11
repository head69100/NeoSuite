using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

public sealed record CleanedItem(LeftoverKind Kind, string Path, string? ValueName, string? BackupFile, bool Success, string? Error);

public sealed record CleanReport(string ProgramName, DateTime Date, string BackupFolder, List<CleanedItem> Items)
{
    public int Succeeded => Items.Count(i => i.Success);
    public int Failed => Items.Count(i => !i.Success);
}

/// <summary>
/// Supprime les restes sélectionnés de façon réversible : fichiers et dossiers dans la Corbeille,
/// registre et services exportés en .reg avant suppression (dossier Sauvegardes, restaurable).
/// </summary>
public static class LeftoverCleaner
{
    public static string BackupsRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoUninstaller", "Backups");

    public static Task<CleanReport> CleanAsync(string programName, IReadOnlyList<Leftover> items, IProgress<int>? progress = null) =>
        Task.Run(() =>
        {
            var date = DateTime.Now;
            var folder = Path.Combine(BackupsRoot, $"{date:yyyyMMdd-HHmmss} {SafeFileName(programName)}");
            Directory.CreateDirectory(folder);

            var results = new List<CleanedItem>();
            var index = 0;
            // Registre d'abord (sauvegarde), puis services, puis fichiers
            foreach (var item in items.OrderBy(i => i.Kind switch { LeftoverKind.RegistryValue => 0, LeftoverKind.RegistryKey => 1, LeftoverKind.Service => 2, LeftoverKind.ScheduledTask => 2, _ => 3 }))
            {
                results.Add(CleanOne(item, folder, ++index));
                progress?.Report(index);
            }

            var report = new CleanReport(programName, date, folder, results);
            File.WriteAllText(Path.Combine(folder, "rapport.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report;
        });

    private static CleanedItem CleanOne(Leftover item, string backupFolder, int index)
    {
        string? backup = null;
        try
        {
            switch (item.Kind)
            {
                case LeftoverKind.Folder:
                    if (Directory.Exists(item.Path))
                        FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    break;

                case LeftoverKind.File:
                case LeftoverKind.Shortcut:
                    if (File.Exists(item.Path))
                        FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    break;

                case LeftoverKind.RegistryKey:
                    backup = Path.Combine(backupFolder, $"{index:D3} cle.reg");
                    ExportKey(item.Path, backup);
                    DeleteKey(item.Path);
                    break;

                case LeftoverKind.RegistryValue:
                    backup = Path.Combine(backupFolder, $"{index:D3} valeur.reg");
                    ExportValue(item.Path, item.ValueName!, backup);
                    using (var key = OpenWritable(item.Path))
                        key?.DeleteValue(item.ValueName!, throwOnMissingValue: false);
                    break;

                case LeftoverKind.Service:
                    var serviceKey = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{item.ValueName}";
                    backup = Path.Combine(backupFolder, $"{index:D3} service.reg");
                    ExportKey(serviceKey, backup);
                    Run("sc.exe", $"stop \"{item.ValueName}\"", ignoreErrors: true);
                    Run("sc.exe", $"delete \"{item.ValueName}\"", ignoreErrors: false);
                    break;

                case LeftoverKind.ScheduledTask:
                    var xml = ScheduledTasks.Export(item.ValueName!) ?? throw new InvalidOperationException("Sauvegarde de la tâche impossible.");
                    backup = Path.Combine(backupFolder, $"{index:D3} tache.xml");
                    File.WriteAllText(backup, xml, Encoding.Unicode);
                    ScheduledTasks.Delete(item.ValueName!);
                    break;
            }
            return new CleanedItem(item.Kind, item.Path, item.ValueName, backup, true, null);
        }
        catch (Exception ex)
        {
            return new CleanedItem(item.Kind, item.Path, item.ValueName, backup, false, Describe(ex));
        }
    }

    /// <summary>Réimporte les clés de registre d'une sauvegarde (les fichiers se restaurent depuis la Corbeille).</summary>
    public static Task<(int Restored, int Failed)> RestoreRegistryAsync(string backupFolder) => Task.Run(() =>
    {
        int ok = 0, failed = 0;
        foreach (var reg in Directory.EnumerateFiles(backupFolder, "*.reg").Order())
        {
            try
            {
                Run("reg.exe", $"import \"{reg}\"", ignoreErrors: false);
                ok++;
            }
            catch (Exception)
            {
                failed++;
            }
        }
        return (ok, failed);
    });

    public static List<CleanReport> LoadReports()
    {
        var reports = new List<CleanReport>();
        if (!Directory.Exists(BackupsRoot))
            return reports;
        foreach (var dir in Directory.EnumerateDirectories(BackupsRoot))
        {
            var file = Path.Combine(dir, "rapport.json");
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<CleanReport>(File.ReadAllText(file)) is { } report)
                    reports.Add(report);
            }
            catch (JsonException)
            {
            }
        }
        return reports.OrderByDescending(r => r.Date).ToList();
    }

    // ---------- Registre ----------

    private static void ExportKey(string fullPath, string file)
    {
        // Sans sauvegarde, pas de suppression : on lève une erreur si l'export échoue
        Run("reg.exe", $"export \"{fullPath}\" \"{file}\" /y /reg:64", ignoreErrors: false);
    }

    /// <summary>Sauvegarde d'une seule valeur (les valeurs Run sont des chaînes) au format .reg.</summary>
    private static void ExportValue(string keyPath, string valueName, string file)
    {
        using var key = OpenReadOnly(keyPath) ?? throw new InvalidOperationException("Clé introuvable.");
        var kind = key.GetValueKind(valueName);
        if (kind != RegistryValueKind.String)
        {
            ExportKey(keyPath, file); // autre type : on sauvegarde toute la clé
            return;
        }
        var data = (string)key.GetValue(valueName)!;
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00").AppendLine();
        sb.AppendLine($"[{keyPath}]");
        sb.AppendLine($"\"{Escape(valueName)}\"=\"{Escape(data)}\"");
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
    }

    private static void DeleteKey(string fullPath)
    {
        var (hive, sub) = Split(fullPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    private static RegistryKey? OpenWritable(string fullPath)
    {
        var (hive, sub) = Split(fullPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(sub, writable: true);
    }

    private static RegistryKey? OpenReadOnly(string fullPath)
    {
        var (hive, sub) = Split(fullPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(sub);
    }

    private static (RegistryHive, string) Split(string fullPath)
    {
        var slash = fullPath.IndexOf('\\');
        var hive = fullPath[..slash] switch
        {
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            var other => throw new InvalidOperationException($"Ruche non prise en charge : {other}"),
        };
        return (hive, fullPath[(slash + 1)..]);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------- Divers ----------

    private static void Run(string exe, string args, bool ignoreErrors)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreErrors)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{exe} a échoué (code {p.ExitCode})." : error.Trim());
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Accès refusé (droits administrateur nécessaires ou fichier protégé).",
        IOException => $"Fichier en cours d'utilisation : {ex.Message}",
        _ => ex.Message,
    };

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
