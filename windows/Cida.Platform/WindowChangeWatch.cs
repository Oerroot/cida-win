using System.Runtime.InteropServices;

namespace Cida.Platform;

public sealed class WindowChangeWatch : IDisposable
{
    private readonly WinEvent _callback;
    private readonly List<nint> _hooks = [];
    public WindowChangeWatch(nint window, Action changed)
    {
        GetWindowThreadProcessId(window, out var process);
        _callback = (_, _, _, _, _, _, _) => changed();
        // Out-of-context hooks execute on the registering message thread; never read UIA in callbacks.
        _hooks.Add(SetWinEventHook(0x800B, 0x8015, 0, _callback, process, 0, 2));
        _hooks.Add(SetWinEventHook(0x0012, 0x0013, 0, _callback, process, 0, 2));
        _hooks.Add(SetWinEventHook(3, 3, 0, _callback, 0, 0, 2));
    }
    public void Dispose() { foreach (var hook in _hooks) if (hook != 0) UnhookWinEvent(hook); _hooks.Clear(); }
    private delegate void WinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
