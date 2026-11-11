using System.Runtime.InteropServices;

namespace Neo.Shared;

/// <summary>
/// Force une fenêtre au premier plan. Nécessaire quand l'appli vient d'être relancée par la tâche
/// planifiée (démarrage administrateur) : Windows refuse alors le premier plan à un processus
/// démarré par l'ordonnanceur de tâches — Activate() seul ne suffit pas et la fenêtre apparaît
/// derrière les autres.
/// </summary>
public static class ForegroundWindow
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    public static void BringToFront(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
        SwitchToThisWindow(hwnd, true);
    }
}
