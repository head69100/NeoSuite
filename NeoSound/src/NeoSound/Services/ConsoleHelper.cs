using System.Runtime.InteropServices;

namespace NeoSound.Services;

/// <summary>Rattache une application fenêtrée (WinExe) à la console qui l'a lancée,
/// pour que les modes « ligne de commande » (--list) puissent écrire dans le terminal.</summary>
internal static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>Réattache la sortie standard à la console parente. Renvoie false si l'appli
    /// a été lancée depuis l'Explorateur (aucune console à rejoindre).</summary>
    public static bool Attach()
    {
        if (!AttachConsole(AttachParentProcess))
            return false;
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(writer);
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(error);
        return true;
    }
}
