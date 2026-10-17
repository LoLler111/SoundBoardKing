using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SoundBoardKing.App.Hotkeys;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("SoundBoardKingHotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>Returns false when the combination is already taken by another application.</summary>
    public bool Register(HotkeyGesture gesture, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_window.Handle, id, gesture.NativeModifiers | ModNoRepeat, gesture.VirtualKey))
            return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
