using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace NeoSoft.Services;

/// <summary>
/// Impr. écran cesse de fonctionner quand NeoSoft (tourné en administrateur) est au premier plan :
/// le raccourci du shell ne voit pas les touches destinées à une fenêtre « élevée » (isolation des
/// privilèges UIPI), tant qu'on n'a pas cliqué dans une fenêtre normale. On observe la touche en amont
/// — un hook bas niveau installé par une appli élevée voit toutes les frappes, c'est autorisé — et,
/// quand c'est NeoSoft qui a le focus, on déclenche nous-mêmes ce que Windows aurait fait : le volet
/// de capture (comportement par défaut, vérifié sur ce PC) ou la copie de l'écran entier dans le
/// presse-papier si le réglage « Utiliser la touche Impr. écran pour ouvrir la capture » est éteint.
/// Les combinaisons (Alt+Impr. écran, Win+Impr. écran) restent intactes.
/// </summary>
internal static class PrintScreenFix
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeydown = 0x0100;
    private const uint WmKeyup = 0x0101;
    private const uint WmSysKeydown = 0x0104;
    private const uint WmSysKeyup = 0x0105;
    private const uint VkSnapshot = 0x2C;
    private const uint VkMenu = 0x12;
    private const uint VkLwin = 0x5B;
    private const uint VkRwin = 0x5C;

    private static IntPtr _hook;
    private static IntPtr _window;
    private static LowLevelProc? _proc; // conservé : le delegate doit vivre autant que le hook

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>Installe l'observation d'Impr. écran pour la fenêtre indiquée (à appeler depuis le thread UI).</summary>
    public static void Install(Window window)
    {
        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _proc = Hook;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Debug.WriteLine($"Impr. écran : hook impossible (erreur {Marshal.GetLastWin32Error()})");
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.ToInt64();
            if (msg is WmKeydown or WmKeyup or WmSysKeydown or WmSysKeyup
                && Marshal.ReadInt32(lParam) == (int)VkSnapshot
                && GetForegroundWindow() == _window
                && !IsDown(VkMenu) && !IsDown(VkLwin) && !IsDown(VkRwin))
            {
                if (msg is WmKeyup or WmSysKeyup)
                    Capture();
                return (IntPtr)1; // absorbée : le système ne la verra pas, pas de déclenchement doublon
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static void Capture()
    {
        // Réglage Windows « Utiliser la touche Impr. écran pour ouvrir la capture » (0 = copie directe).
        // Absent = comportement par défaut, qui est le volet de capture (constaté sur ce PC).
        var snipping = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            snipping = key?.GetValue("PrintScreenKeyForSnippingEnabled") is not int mode || mode != 0;
        }
        catch (Exception)
        {
            // Réglage illisible : on garde le volet de capture
        }

        if (snipping)
        {
            // Exactement le même volet que le raccourci système (fonctionne vu depuis un processus admin)
            Process.Start(new ProcessStartInfo("explorer.exe", "ms-screenclip:") { UseShellExecute = true });
        }
        else
        {
            CopyScreenToClipboard();
        }
    }

    // ---------- Copie directe (réglage Windows désactivé) ----------

    private const uint Srccopy = 0x00CC0020;
    private const uint CfBitmap = 2;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    /// <summary>Capture tous les moniteurs (écran virtuel) et place le bitmap dans le presse-papier.</summary>
    private static void CopyScreenToClipboard()
    {
        var x = GetSystemMetrics(SmXvirtualscreen);
        var y = GetSystemMetrics(SmYvirtualscreen);
        var w = GetSystemMetrics(SmCxvirtualscreen);
        var h = GetSystemMetrics(SmCyvirtualscreen);
        if (w <= 0 || h <= 0)
            return;

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var previous = SelectObject(mem, bitmap);
        BitBlt(mem, 0, 0, w, h, screen, x, y, Srccopy);
        SelectObject(mem, previous);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);

        var opened = false;
        for (var attempt = 0; attempt < 5 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened)
                Thread.Sleep(20);
        }
        if (opened && EmptyClipboard() && SetClipboardData(CfBitmap, bitmap) != IntPtr.Zero)
        {
            // Le presse-papier est désormais propriétaire du bitmap : ne pas le libérer
            CloseClipboard();
            return;
        }
        if (opened)
            CloseClipboard();
        DeleteObject(bitmap);
    }
}
