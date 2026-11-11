using System.Runtime.InteropServices;

namespace Neo.Shared;

/// <summary>
/// Fenêtre Win32 invisible qui reçoit des messages système (clics sur l'icône de la barre système,
/// raccourcis clavier globaux…). Doit être créée sur le thread de l'interface.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    public delegate bool MessageHandler(uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProc _wndProc; // référence conservée pour le GC
    private readonly string _className;
    private readonly MessageHandler _handler;

    public IntPtr Handle { get; }

    public MessageWindow(string name, MessageHandler handler)
    {
        _handler = handler;
        _wndProc = WindowProc;
        _className = $"Neo_{name}_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);
        Handle = CreateWindowEx(0, _className, name, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        _handler(msg, wParam, lParam) ? IntPtr.Zero : DefWindowProc(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        DestroyWindow(Handle);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
