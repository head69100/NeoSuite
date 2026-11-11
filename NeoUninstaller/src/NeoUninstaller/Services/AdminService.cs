using System.ComponentModel;
using System.Diagnostics;
using Neo.Shared;

namespace NeoUninstaller.Services;

public static class AdminService
{
    public static bool IsAdministrator => ElevatedLaunch.IsAdministrator;

    /// <summary>
    /// Relance NeoUninstaller en administrateur : via la tâche planifiée si elle est active (sans UAC),
    /// sinon avec l'invite UAC. Renvoie false si l'utilisateur refuse.
    /// </summary>
    public static bool RelaunchElevated(string arguments)
    {
        // La nouvelle instance attendra que celle-ci se ferme (instance unique)
        arguments += " --relaunched";

        if (ElevatedLaunch.IsInstalledCopy("NeoUninstaller") && ElevatedLaunch.TryLaunch("NeoUninstaller", arguments))
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
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}
