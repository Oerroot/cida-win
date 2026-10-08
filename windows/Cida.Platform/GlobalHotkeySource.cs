using System.Runtime.InteropServices;

namespace Cida.Platform;

/// <summary>
/// Global hotkeys through <c>RegisterHotKey</c>. Windows maps the macOS option modifier to
/// Alt. Ported from upstream GlobalHotKey.swift (Carbon RegisterEventHotKey).
/// </summary>
public sealed class GlobalHotkeySource : IDisposable
{
    /// <summary>A hotkey id plus the action it triggers.</summary>
    public sealed record Registration(int Id, GlobalShortcutActionMirror Action);

    /// <summary>Mirrors Cida.Core.GlobalShortcutAction without taking a dependency on its shape.</summary>
    public enum GlobalShortcutActionMirror
    {
        ShowPanel,
        CaptureText,
        TranslationLayer,
        WholeWindowTranslationLayer,
        ImproveAndReplace,
    }

    public const int WmHotkey = 0x0312;

    private const int WM_HOTKEY = 0x0312;

    [Flags]
    private enum Modifiers : uint
    {
        None = 0,
        Alt = 0x1,
        Control = 0x2,
        Shift = 0x4,
        Win = 0x8,
    }

    private readonly nint _hwnd;
    private readonly Dictionary<int, GlobalShortcutActionMirror> _actions = [];
    private readonly WndProcDelegate _wndProc;
    private int _nextId = 0x4944; // "ID"

    public event Action<GlobalShortcutActionMirror>? HotkeyPressed;

    public GlobalHotkeySource()
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
    }

    /// <summary>Registers a shortcut; returns false when Windows refuses (a conflict).</summary>
    public bool Register(ushort keyCode, byte modifiers, GlobalShortcutActionMirror action)
    {
        var id = _nextId++;
        var windowsModifiers = Translate(modifiers);
        if (!RegisterHotKey(_hwnd, id, (uint)windowsModifiers | 0x4000 /* MOD_NOREPEAT */, keyCode))
        {
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _actions.Clear();
    }

    private static Modifiers Translate(byte modifiers)
    {
        var result = Modifiers.None;
        if ((modifiers & (byte)ShortcutModifiersMirror.Control) != 0) result |= Modifiers.Control;
        if ((modifiers & (byte)ShortcutModifiersMirror.Alt) != 0) result |= Modifiers.Alt;
        if ((modifiers & (byte)ShortcutModifiersMirror.Shift) != 0) result |= Modifiers.Shift;
        if ((modifiers & (byte)ShortcutModifiersMirror.Win) != 0) result |= Modifiers.Win;
        return result;
    }

    private enum ShortcutModifiersMirror
    {
        None = 0,
        Control = 1 << 0,
        Alt = 1 << 1,
        Shift = 1 << 2,
        Win = 1 << 3,
    }

    // MARK: message-only window

    private nint CreateMessageWindow()
    {
        var name = "CidaHotkey-" + Guid.NewGuid().ToString("N");
        var Class = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszMenuName = "",
            lpszClassName = name,
        };
        var atom = RegisterClass(ref Class);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterClass failed: {error}");
        }
        var window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandle(null), 0);
        if (window == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowEx failed: {error}");
        }
        return window;
    }

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                HotkeyPressed?.Invoke(action);
            }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyWindow(_hwnd);
    }

    // MARK: Win32

    private static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
