using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT;

namespace NeoSuite;

/// <summary>
/// Menu clic droit de l'icône NeoSuite, façon Windows 11. Comme un vrai menu Windows, il ne prend
/// pas le focus (sinon le panneau des icônes cachées ^ se fermerait) : la fermeture au clic extérieur
/// et à Échap passe par des hooks souris/clavier tant qu'il est affiché.
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private const int MenuWidth = 280;
    private const int MenuHeight = 214;

    private readonly MainWindow _main;
    private readonly IntPtr _hwnd;
    private readonly DesktopAcrylicController _acrylic;

    public TrayMenuWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        var round = 2; // coins arrondis
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        var noTransitions = 1; // pas d'image fantôme à la fermeture
        DwmSetWindowAttribute(_hwnd, 3, ref noTransitions, sizeof(int));

        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark });

        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HideNow();
        };
    }

    /// <summary>Affiche le menu près du curseur (coordonnées écran en pixels physiques).</summary>
    public void ShowAt(int cursorX, int cursorY)
    {
        var hotKey = _main.CurrentHotKey?.ToString();
        ShortcutText.Text = hotKey ?? "";
        SubtitleText.Text = hotKey is null ? "Vos applications Neo" : $"Raccourci : {hotKey}";

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)(MenuWidth * scale), h = (int)(MenuHeight * scale);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursorX, cursorY), DisplayAreaFallback.Nearest).WorkArea;

        // Placement des menus Windows : coin sur le curseur, ouvert vers le haut et la droite si possible
        var x = cursorX + w <= work.X + work.Width ? cursorX : cursorX - w;
        var y = cursorY - h >= work.Y ? cursorY - h : cursorY;
        x = Math.Clamp(x, work.X, work.X + work.Width - w);
        y = Math.Clamp(y, work.Y, work.Y + work.Height - h);

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        AppWindow.Show(activateWindow: false);
        InstallHooks();
    }

    private void HideMenu() => DispatcherQueue.TryEnqueue(HideNow);

    private void HideNow()
    {
        RemoveHooks();
        AppWindow.Hide();
    }

    private void Run(Action action)
    {
        HideNow();
        action();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Run(() => _main.OpenPanel());

    private void Settings_Click(object sender, RoutedEventArgs e) => Run(() => _main.OpenPanel(showSettings: true));

    private void Quit_Click(object sender, RoutedEventArgs e) => Run(_main.Quit);

    // ---------- Fermeture au clic en dehors et avec Échap ----------

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private IntPtr _mouseHook, _keyboardHook;
    private HookProc? _mouseProc, _keyboardProc;

    private void InstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
            return;
        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (_mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
        if (code >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            var pt = Marshal.PtrToStructure<POINT>(lParam); // MSLLHOOKSTRUCT commence par le point
            GetWindowRect(_hwnd, out var r);
            if (pt.X < r.Left || pt.X >= r.Right || pt.Y < r.Top || pt.Y >= r.Bottom)
                HideMenu();
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
        if (code >= 0 && (int)wParam == WM_KEYDOWN && Marshal.ReadInt32(lParam) == VK_ESCAPE)
            HideMenu();
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
