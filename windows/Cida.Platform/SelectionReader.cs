using System.Runtime.InteropServices;

namespace Cida.Platform;

/// <summary>Reads the focused selection, with a bounded copy fallback that preserves clipboard formats.</summary>
public sealed class SelectionReader
{
    public static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(600);
    public sealed record Result(string? Text, Result.OutcomeKind Kind, string Strategy)
    {
        public enum OutcomeKind { Read, NoSelection, Unreadable, Protected }
    }
    public static nint ForegroundWindow() => GetForegroundWindow();

    public Result ReadFromAutomation(nint window)
    {
        if (window == 0) return Unreadable("automation");
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(window);
            if (root == null) return Unreadable("automation");
            var focus = System.Windows.Automation.AutomationElement.FocusedElement;
            var ancestors = new List<System.Windows.Automation.AutomationElement>();
            var walker = System.Windows.Automation.TreeWalker.RawViewWalker;
            for (var current = focus; current != null; current = walker.GetParent(current))
            {
                ancestors.Add(current);
                if (System.Windows.Automation.Automation.Compare(current, root)) break;
            }
            if (ancestors.Count > 0 && System.Windows.Automation.Automation.Compare(ancestors[^1], root))
            {
                foreach (var target in ancestors)
                {
                    if (target.Current.IsPassword) return new(null, Result.OutcomeKind.Protected, "automation");
                    if (target.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var pattern)
                        && pattern is System.Windows.Automation.TextPattern textPattern)
                        return FromPattern(textPattern);
                }
            }
            // A provider may expose its selected range without exposing keyboard focus.
            var elements = root.FindAll(System.Windows.Automation.TreeScope.Subtree,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.IsTextPatternAvailableProperty, true));
            foreach (System.Windows.Automation.AutomationElement target in elements)
            {
                if (target.Current.IsPassword) continue;
                if (target.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var pattern)
                    && pattern is System.Windows.Automation.TextPattern textPattern)
                {
                    var result = FromPattern(textPattern);
                    if (result.Kind == Result.OutcomeKind.Read) return result;
                }
            }
        }
        catch (COMException) { }
        catch (InvalidOperationException) { }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
        return Unreadable("automation");
    }

    private static Result FromPattern(System.Windows.Automation.TextPattern pattern)
    {
        var text = string.Concat(pattern.GetSelection().Select(range => range.GetText(int.MaxValue)));
        return new(text.Length == 0 ? null : text,
            text.Length == 0 ? Result.OutcomeKind.NoSelection : Result.OutcomeKind.Read, "automation");
    }
    private static Result Unreadable(string strategy) => new(null, Result.OutcomeKind.Unreadable, strategy);

    public Result ReadByCopying(nint window, CancellationToken cancellationToken = default)
    {
        if (window == 0) return Unreadable("copy");
        try
        {
            using var before = ClipboardSnapshot.Capture();
            // Never synthesize a copy if any original format could not be preserved.
            if (before == null || ClipboardSnapshot.SequenceNumber() != before.Sequence
                || !RestoreFocusAndCopy(window, cancellationToken)) return Unreadable("copy");
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(25);
                var sequence = ClipboardSnapshot.SequenceNumber();
                if (sequence == before.Sequence) continue;
                var text = ClipboardSnapshot.ReadText(window, sequence);
                if (text == null) return Unreadable("copy");
                // The restore acquires the clipboard lock and checks sequence and owner atomically.
                ClipboardSnapshot.Restore(before, window, sequence);
                return new(text, text.Length == 0 ? Result.OutcomeKind.NoSelection : Result.OutcomeKind.Read, "copy");
            }
            var finalSequence = ClipboardSnapshot.SequenceNumber();
            if (finalSequence != before.Sequence) ClipboardSnapshot.Restore(before, window, finalSequence);
            return Unreadable("copy");
        }
        finally { ClipboardSnapshot.ReleaseWindow(); }
    }

    public Result Read(nint window, CancellationToken cancellationToken = default)
    {
        var automated = ReadFromAutomation(window);
        return automated.Kind != Result.OutcomeKind.Unreadable ? automated : ReadByCopying(window, cancellationToken);
    }

    private static bool RestoreFocusAndCopy(nint window, CancellationToken cancellationToken)
    {
        if (GetForegroundWindow() != window && !SetForegroundWindow(window)) return false;
        var deadline = DateTime.UtcNow + Deadline;
        while (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
        {
            if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested) return false;
            Thread.Sleep(10);
        }
        if (GetForegroundWindow() != window || cancellationToken.IsCancellationRequested) return false;
        var inputs = new[] { KeyInput(0x11, false), KeyInput(0x43, false), KeyInput(0x43, true), KeyInput(0x11, true) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }
    private static INPUT KeyInput(ushort key, bool up) => new()
    { type = 1, U = new INPUTUNION { keyboard = new KEYBDINPUT { wVk = key, dwFlags = up ? 2u : 0u } } };

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public HARDWAREINPUT hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT
    { public uint uMsg; public ushort wParamL, wParamH; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

internal static class ClipboardSnapshot
{
    [ThreadStatic] private static nint _hwnd;
    [ThreadStatic] internal static string? LastCaptureFailure;
    private static nint Window => _hwnd != 0 ? _hwnd : _hwnd = CreateWindowEx(
        0, "STATIC", "CidaClipboard", 0, 0, 0, 0, 0, new nint(-3), 0, GetModuleHandle(null), 0);
    public static void ReleaseWindow() { if (_hwnd != 0) DestroyWindow(_hwnd); _hwnd = 0; }
    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    public sealed class Snapshot(uint sequence) : IDisposable
    {
        public uint Sequence { get; } = sequence;
        public List<(uint Format, nint Handle)> Formats { get; } = [];
        public void Dispose()
        {
            foreach (var (format, handle) in Formats) if (handle != 0) Free(format, handle);
            Formats.Clear();
        }
    }
    public static Snapshot? Capture()
    {
        LastCaptureFailure = null;
        if (Window == 0 || !TryOpen()) { LastCaptureFailure = "clipboard unavailable"; return null; }
        var snapshot = new Snapshot(SequenceNumber());
        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                // Owner-display has no independently copyable data.
                if (format == 0x80 || format is >= 0x200 and <= 0x3FF)
                { LastCaptureFailure = $"unsupported format {format}"; snapshot.Dispose(); return null; }
                var data = GetClipboardData(format);
                var duplicate = data == 0 ? 0 : format is 14 or 0x8E
                    ? CopyEnhMetaFile(data, null)
                    : OleDuplicateData(data, format == 0x82 ? 2u : format == 0x83 ? 3u : format, 2);
                if (duplicate == 0) { LastCaptureFailure = $"cannot duplicate format {format}"; snapshot.Dispose(); return null; }
                snapshot.Formats.Add((format, duplicate));
            }
            if (Marshal.GetLastWin32Error() != 0) { LastCaptureFailure = "format enumeration failed"; snapshot.Dispose(); return null; }
            return snapshot;
        }
        catch { snapshot.Dispose(); return null; }
        finally { CloseClipboard(); }
    }
    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(Window)) return true;
            Thread.Sleep(10);
        }
        return false;
    }
    private static bool IsSourceOwner(nint source)
    {
        var owner = GetClipboardOwner();
        // OLE can flush copied data and release its owner HWND. Require the source to
        // remain foreground in that case; sequence checks still protect newer copies.
        if (owner == 0) return GetForegroundWindow() == source;
        GetWindowThreadProcessId(source, out var sourcePid);
        GetWindowThreadProcessId(owner, out var ownerPid);
        return sourcePid != 0 && sourcePid == ownerPid;
    }
    public static string? ReadText(nint source, uint expectedSequence)
    {
        if (!TryOpen()) return null;
        try
        {
            if (SequenceNumber() != expectedSequence || !IsSourceOwner(source)) return null;
            var handle = GetClipboardData(13);
            var pointer = handle == 0 ? 0 : GlobalLock(handle);
            if (pointer == 0) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    public static bool Restore(Snapshot snapshot, nint source, uint expectedSequence)
    {
        if (!TryOpen()) return false;
        try
        {
            if (SequenceNumber() != expectedSequence || !IsSourceOwner(source)) return false;
            if (!EmptyClipboard()) return false;
            var restored = true;
            for (var i = 0; i < snapshot.Formats.Count; i++)
            {
                var entry = snapshot.Formats[i];
                if (SetClipboardData(entry.Format, entry.Handle) != 0)
                    snapshot.Formats[i] = (entry.Format, 0); // Ownership transferred to Windows.
                else restored = false;
            }
            return restored;
        }
        finally { CloseClipboard(); }
    }
    private static void Free(uint format, nint handle)
    {
        switch (format)
        {
            case 2: case 9: case 0x82: DeleteObject(handle); break;
            case 14: case 0x8E: DeleteEnhMetaFile(handle); break;
            case 3: case 0x83:
                var pointer = GlobalLock(handle);
                if (pointer != 0)
                {
                    DeleteMetaFile(Marshal.PtrToStructure<METAFILEPICT>(pointer).hMF);
                    GlobalUnlock(handle);
                }
                GlobalFree(handle);
                break;
            default: GlobalFree(handle); break;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct METAFILEPICT { public int mm, xExt, yExt; public nint hMF; }
    [DllImport("ole32.dll")] private static extern nint OleDuplicateData(nint source, uint format, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteMetaFile(nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(nint handle);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CopyEnhMetaFile(nint source, string? file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(
        uint style, string className, string name, uint windowStyle, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
}
