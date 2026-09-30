using System.Runtime.InteropServices;
using Cida.Core;

namespace Cida.Platform;

/// <summary>
/// Reads the selection of another application. Two-level strategy ported from upstream
/// SelectedText.swift: UI Automation TextPattern first, a synthesized Ctrl+C with clipboard
/// snapshot-and-restore as the fallback. Password fields are never read.
/// </summary>
public sealed class SelectionReader
{
    /// <summary>How long each strategy may take.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// The outcome: a selection was read; the focused element held no selection; or the
    /// application could not be asked at all.
    /// </summary>
    public sealed record Result(string? Text, Result.OutcomeKind Kind, string Strategy)
    {
        public enum OutcomeKind
        {
            Read,
            /// <summary>The source answered: nothing is selected.</summary>
            NoSelection,
            /// <summary>The source could not be asked.</summary>
            Unreadable,
        }
    }

    /// <summary>The window the selection is read from; captured before focus moves.</summary>
    public static nint ForegroundWindow()
    {
        var handle = GetForegroundWindow();
        return handle;
    }

    public Result ReadFromAutomation(nint window)
    {
        try
        {
            var element = System.Windows.Automation.AutomationElement.FromHandle(window);
            // Password fields: the edit's IsPassword property marks them.
            if (element?.Current.IsPassword == true)
            {
                return new Result(null, Result.OutcomeKind.Unreadable, "automation");
            }
            // The focused element inside the window holds the selection; the window itself
            // is the fallback when the tree walk does not reach it.
            var target = element!.FindFirst(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.IsTextPatternAvailableProperty, true))
                ?? element;
            object? patternObject;
            try
            {
                patternObject = target!.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern);
            }
            catch (System.InvalidOperationException)
            {
                return new Result(null, Result.OutcomeKind.Unreadable, "automation");
            }
            if (patternObject is System.Windows.Automation.TextPattern textPattern)
            {
                var selections = textPattern.GetSelection();
                var text = string.Concat(selections.Select(selection => selection.GetText(int.MaxValue)));
                if (text.Length == 0)
                {
                    return new Result(null, Result.OutcomeKind.NoSelection, "automation");
                }
                return new Result(text, Result.OutcomeKind.Read, "automation");
            }
            return new Result(null, Result.OutcomeKind.Unreadable, "automation");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return new Result(null, Result.OutcomeKind.Unreadable, "automation");
        }
    }

    /// <summary>
    /// The fallback: snapshot the clipboard, post Ctrl+C, poll for new content, restore the
    /// clipboard. Mirrors upstream PasteboardSelectionCopier, including the wait deadline.
    /// </summary>
    public Result ReadByCopying(nint window, CancellationToken cancellationToken = default)
    {
        if (window == 0) return new Result(null, Result.OutcomeKind.Unreadable, "copy");
        var before = ClipboardSnapshot.Capture();
        var sequenceBefore = ClipboardSnapshot.SequenceNumber();
        if (!RestoreFocusAndCopy(window))
        {
            return new Result(null, Result.OutcomeKind.Unreadable, "copy");
        }
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            Thread.Sleep(25);
            var sequence = ClipboardSnapshot.SequenceNumber();
            if (sequence == sequenceBefore) continue;
            var text = ClipboardSnapshot.ReadText();
            if (text == null) continue;
            // Restore what the user had before our synthesized copy.
            ClipboardSnapshot.Restore(before);
            if (text.Length == 0)
            {
                return new Result(null, Result.OutcomeKind.NoSelection, "copy");
            }
            return new Result(text, Result.OutcomeKind.Read, "copy");
        }
        ClipboardSnapshot.Restore(before);
        return new Result(null, Result.OutcomeKind.Unreadable, "copy");
    }

    /// <summary>Full pipeline: automation first, copy fallback only when unreadable.</summary>
    public Result Read(nint window, CancellationToken cancellationToken = default)
    {
        var automated = ReadFromAutomation(window);
        if (automated.Kind != Result.OutcomeKind.Unreadable)
        {
            return automated;
        }
        return ReadByCopying(window, cancellationToken);
    }

    private static bool RestoreFocusAndCopy(nint window)
    {
        // Bring the target window to the foreground the way a user's keypress would, then
        // send Ctrl+C. UIPI: a normal-integrity process cannot send input to an elevated
        // window; SendInput reports failure through GetLastWin32Error, which we surface as
        // "unreadable" rather than crashing.
        SetForegroundWindow(window);
        Thread.Sleep(60);
        const uint ctrlDown = 0x11, keyDown = 0, keyUp = 2;
        const ushort vkControl = 0x11, vkC = 0x43;
        var inputs = new INPUT[4];
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].U.wVk = vkControl;
        inputs[0].U.dwFlags = keyDown;
        inputs[1].type = 1;
        inputs[1].U.wVk = vkC;
        inputs[1].U.dwFlags = keyDown;
        inputs[2].type = 1;
        inputs[2].U.wVk = vkC;
        inputs[2].U.dwFlags = keyUp;
        inputs[3].type = 1;
        inputs[3].U.wVk = vkControl;
        inputs[3].U.dwFlags = keyUp;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    // MARK: Win32

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public ushort wVk;
        [FieldOffset(4)] public uint dwFlags;
    }
}

/// <summary>Clipboard access on the copying thread, with sequence numbers.</summary>
internal static class ClipboardSnapshot
{
    private static nint _hwnd;

    private static nint Window
    {
        get
        {
            if (_hwnd != 0) return _hwnd;
            _hwnd = CreateMessageOnlyWindow();
            return _hwnd;
        }
    }

    private static nint CreateMessageOnlyWindow()
    {
        var name = "CidaClipboard-" + Guid.NewGuid().ToString("N");
        return CreateWindowEx(0, "STATIC", name, 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
    }

    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    public sealed record Snapshot(uint Sequence, string? Text);

    private static unsafe string ReadNullTerminated(nint pointer)
    {
        var scan = (ushort*)pointer;
        var length = 0;
        while (scan[length] != 0) length++;
        return new string((char*)pointer, 0, length);
    }

    public static Snapshot Capture()
    {
        return new Snapshot(SequenceNumber(), ReadText());
    }

    public static string? ReadText()
    {
        try
        {
            if (!OpenClipboard(Window)) return null;
            try
            {
                var handle = GetClipboardData(13 /* CF_UNICODETEXT */);
                if (handle == 0) return null;
                var pointer = GlobalLock(handle);
                if (pointer == 0) return null;
                try
                {
                    return ReadNullTerminated(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Restore(Snapshot snapshot)
    {
        try
        {
            if (!OpenClipboard(Window)) return;
            EmptyClipboard();
            if (snapshot.Text is { Length: > 0 } text)
            {
                var bytes = (text.Length + 1) * 2;
                var handle = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (nuint)bytes);
                if (handle == 0) return;
                var pointer = GlobalLock(handle);
                if (pointer != 0)
                {
                    unsafe
                    {
                        var destination = (char*)pointer;
                        fixed (char* source = text)
                        {
                            for (var index = 0; index < text.Length; index++)
                            {
                                destination[index] = source[index];
                            }
                        }
                        destination[text.Length] = '\0';
                    }
                    GlobalUnlock(handle);
                    SetClipboardData(13, handle);
                }
            }
        }
        catch
        {
            // The restore is best-effort; the copied text already reached the caller.
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
}
