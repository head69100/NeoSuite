using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace NeoDriver.Services;

/// <summary>Empêche de réduire une fenêtre en dessous d'une taille minimale (WM_GETMINMAXINFO).</summary>
public sealed class WindowMinSize
{
    private const uint WM_GETMINMAXINFO = 0x0024;

    private readonly SubclassProc _proc; // référence conservée pour le GC
    private readonly int _minWidth;
    private readonly int _minHeight;

    /// <param name="minWidth">Largeur minimale en pixels logiques (mise à l'échelle selon le DPI).</param>
    public WindowMinSize(Window window, int minWidth, int minHeight)
    {
        _minWidth = minWidth;
        _minHeight = minHeight;
        _proc = Proc;
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(window), _proc, 1, 0);
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var scale = GetDpiForWindow(hwnd) / 96.0;
            // MINMAXINFO.ptMinTrackSize se trouve après 3 POINT (3 × 8 octets)
            Marshal.WriteInt32(lParam, 24, (int)(_minWidth * scale));
            Marshal.WriteInt32(lParam, 28, (int)(_minHeight * scale));
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
