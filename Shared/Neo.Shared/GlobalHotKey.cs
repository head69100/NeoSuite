using System.Runtime.InteropServices;

namespace Neo.Shared;

/// <summary>Raccourci clavier global (fonctionne même quand l'appli n'a pas le focus), via RegisterHotKey.</summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const int HotKeyId = 1;

    private readonly MessageWindow _window;
    private bool _registered;

    public event EventHandler? Pressed;

    public HotKeyGesture? Current { get; private set; }

    public GlobalHotKey()
    {
        _window = new MessageWindow("HotKey", (msg, wParam, _) =>
        {
            if (msg != WM_HOTKEY || wParam.ToInt32() != HotKeyId)
                return false;
            Pressed?.Invoke(this, EventArgs.Empty);
            return true;
        });
    }

    /// <summary>Enregistre le raccourci. Renvoie false s'il est déjà pris par une autre application.</summary>
    public bool Register(HotKeyGesture gesture)
    {
        Unregister();
        if (!gesture.IsValid)
            return false;
        _registered = RegisterHotKey(_window.Handle, HotKeyId, (int)gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey);
        Current = _registered ? gesture : null;
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
            UnregisterHotKey(_window.Handle, HotKeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
