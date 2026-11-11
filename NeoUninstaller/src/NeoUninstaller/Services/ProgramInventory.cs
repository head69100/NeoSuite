using System.Globalization;
using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>Liste des programmes installés, lue dans les clés Uninstall du registre (64 bits, 32 bits, utilisateur).</summary>
public static class ProgramInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static Task<List<InstalledProgram>> LoadAsync() => Task.Run(Load);

    public static List<InstalledProgram> Load()
    {
        var programs = new List<InstalledProgram>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, programs);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, programs);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, programs);
        Read(RegistryHive.CurrentUser, RegistryView.Registry32, programs);

        // Un même programme peut apparaître dans plusieurs vues : on garde la première
        return programs
            .GroupBy(p => (p.DisplayName.ToLowerInvariant(), p.Version))
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>L'entrée du programme est-elle encore dans le registre ? (après la désinstallation)</summary>
    public static bool StillInstalled(InstalledProgram program)
    {
        if (program.SubKeyName.StartsWith("forced:"))
            return false; // désinstallation forcée d'un programme absent du registre
        using var root = RegistryKey.OpenBaseKey(program.Hive, program.View);
        using var key = root.OpenSubKey($@"{UninstallPath}\{program.SubKeyName}");
        return key is not null;
    }

    private static void Read(RegistryHive hive, RegistryView view, List<InstalledProgram> programs)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null)
                return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null || !IsUserVisible(key))
                    continue;

                var uninstallString = key.GetValue("UninstallString") as string;
                programs.Add(new InstalledProgram
                {
                    Hive = hive,
                    View = view,
                    SubKeyName = name,
                    DisplayName = ((string)key.GetValue("DisplayName")!).Trim(),
                    Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                    Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
                    InstallDate = ParseDate(key.GetValue("InstallDate") as string),
                    SizeBytes = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null,
                    InstallLocation = CleanPath(key.GetValue("InstallLocation") as string),
                    DisplayIcon = key.GetValue("DisplayIcon") as string,
                    UninstallString = uninstallString,
                    QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                    IsMsi = key.GetValue("WindowsInstaller") is 1
                            || uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true,
                });
            }
        }
        catch (Exception)
        {
            // Vue inexistante (ex. HKCU 32 bits) ou accès refusé : on passe
        }
    }

    /// <summary>Mêmes règles que « Applications installées » : pas de composants système ni de mises à jour.</summary>
    private static bool IsUserVisible(RegistryKey key)
    {
        if (key.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
            return false;
        if (key.GetValue("SystemComponent") is 1)
            return false;
        if (key.GetValue("ParentKeyName") is string)
            return false;
        if (key.GetValue("ReleaseType") is string type && type.Contains("Update", StringComparison.OrdinalIgnoreCase) || key.GetValue("ReleaseType") is "Hotfix")
            return false;
        return key.GetValue("UninstallString") is string;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? CleanPath(string? path)
    {
        path = path?.Trim().Trim('"').TrimEnd('\\');
        return string.IsNullOrEmpty(path) ? null : Environment.ExpandEnvironmentVariables(path);
    }
}
