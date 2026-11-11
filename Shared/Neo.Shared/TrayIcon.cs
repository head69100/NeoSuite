using System.Runtime.InteropServices;

namespace Neo.Shared;

public sealed class TrayMenuRequestedEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
}

/// <summary>
/// Icône de la zone de notification (barre système), via Shell_NotifyIcon.
/// WinUI 3 n'en fournit pas : une fenêtre Win32 invisible reçoit les clics.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public event EventHandler? OpenRequested;
    /// <summary>Clic droit : position du curseur en pixels écran, pour afficher le menu.</summary>
    public event EventHandler<TrayMenuRequestedEventArgs>? MenuRequested;

    private const int WM_APP_TRAY = 0x8001;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const int NOTIFYICON_VERSION_4 = 4;

    private readonly MessageWindow _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private bool _visible;

    public TrayIcon(string iconPath, string tooltip)
    {
        _window = new MessageWindow("Tray", HandleMessage);
        // Taille d'icône système (16 px à 100 %, plus en haute résolution)
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Tooltip = tooltip;
    }

    public string Tooltip { get; private set; }

    public void Show()
    {
        if (_visible)
            return;
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible)
            return;
        var data = CreateData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _visible = false;
    }

    public void UpdateTooltip(string tooltip)
    {
        Tooltip = tooltip;
        if (!_visible)
            return;
        var data = CreateData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Notification Windows affichée depuis l'icône.</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_visible)
            return;
        var data = CreateData(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Tooltip.Length > 127 ? Tooltip[..127] : Tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            // Version 4 : l'événement est dans le mot bas de lParam ; un clic gauche envoie NIN_SELECT,
            // un clic droit WM_CONTEXTMENU (les WM_xBUTTONUP bruts sont ignorés pour ne pas réagir deux fois)
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_CONTEXTMENU:
                    GetCursorPos(out var pt);
                    MenuRequested?.Invoke(this, new TrayMenuRequestedEventArgs(pt.X, pt.Y));
                    break;
            }
            return true;
        }

        // Explorer a redémarré : l'icône doit être recréée
        if (msg == _taskbarCreated && _visible)
        {
            _visible = false;
            Show();
        }
        return false;
    }

    public void Dispose()
    {
        Hide();
        _window.Dispose();
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
