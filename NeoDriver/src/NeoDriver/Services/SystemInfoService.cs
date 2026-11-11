using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoDriver.Services;

public static class SystemInfoService
{
    public static string GetOsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(key?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        var edition = (key?.GetValue("EditionID") as string) switch
        {
            "Core" or "CoreSingleLanguage" => "Famille",
            "Professional" => "Pro",
            "Enterprise" => "Entreprise",
            "Education" => "Éducation",
            var other => other ?? "",
        };
        var displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} {edition} {displayVersion}".Trim();
    }

    public static string GetOsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR");
        return ubr is null ? build : $"{build}.{ubr}";
    }

    public static string GetArchitecture() => RuntimeInformation.OSArchitecture.ToString();

    public static string GetProcessorName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Inconnu";
    }

    /// <summary>Nombre de pilotes signés installés (requête WMI, peut prendre quelques secondes).</summary>
    public static Task<int> CountInstalledDriversAsync() => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");
        using var results = searcher.Get();
        return results.Count;
    });
}
