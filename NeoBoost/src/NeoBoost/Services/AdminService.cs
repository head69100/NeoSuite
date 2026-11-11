using System.ComponentModel;
using System.Diagnostics;
using Neo.Shared;

namespace NeoBoost.Services;

public static class AdminService
{
    public static bool IsAdministrator => ElevatedLaunch.IsAdministrator;

    /// <summary>
    /// Relance NeoBoost en administrateur : via la tâche planifiée si elle est active (sans UAC),
    /// sinon avec l'invite UAC. Renvoie false si l'utilisateur refuse.
    /// </summary>
    public static bool RelaunchElevated(string arguments)
    {
        // La nouvelle instance attendra que celle-ci se ferme (instance unique)
        arguments += " --relaunched";

        if (ElevatedLaunch.IsInstalledCopy("NeoBoost") && ElevatedLaunch.TryLaunch("NeoBoost", arguments))
            return true;

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Ouvre NeoUninstaller (s'il est installé) sur une page donnée. Renvoie false s'il est absent.</summary>
    public static bool OpenNeoUninstaller(string page)
    {
        var exe = Path.Combine(ElevatedLaunch.InstallDir("NeoUninstaller"), "NeoUninstaller.exe");
        if (!File.Exists(exe))
            return false;
        Process.Start(new ProcessStartInfo(exe, $"--page={page}") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        return true;
    }
}
