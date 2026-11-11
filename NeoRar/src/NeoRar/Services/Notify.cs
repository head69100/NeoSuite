using System.Runtime.InteropServices;

namespace NeoRar.Services;

/// <summary>
/// Notification « bulle » de Windows au-dessus de la barre des tâches : ni application empaquetée ni
/// bibliothèque supplémentaire, juste Shell_NotifyIcon posé sur une fenêtre message invisible.
/// </summary>
public static class Notify
{
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint WmTimer = 0x0113;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;

    /// <summary>Affiche une bulle pendant environ six secondes. Silencieux si Windows refuse l'icône.</summary>
    public static void Show(string title, string message)
    {
        var thread = new Thread(() => Run(title, message))
        {
            IsBackground = true,
            Name = "NeoRar notification",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(string title, string message)
    {
        var hwnd = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            return;

        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NifTip | NifInfo,
            szTip = "NeoRar",
            szInfoTitle = Truncate(title, 63),
            szInfo = Truncate(message, 255),
            dwInfoFlags = NiifInfo,
        };
        var icon = LoadImageW(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ImageIcon, 0, 0, LrLoadFromFile);
        if (icon != IntPtr.Zero)
        {
            data.uFlags |= NifIcon;
            data.hIcon = icon;
        }

        try
        {
            if (!Shell_NotifyIconW(NimAdd, ref data))
                return;
            SetTimer(hwnd, 1, 6000, IntPtr.Zero);
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WmTimer)
                    break;
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Shell_NotifyIconW(NimDelete, ref data);
        }
        finally
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
            DestroyWindow(hwnd);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point point;
        public uint lPrivate;

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int x;
            public int y;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, [In] ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref Message lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW([In] ref Message lpMsg);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
