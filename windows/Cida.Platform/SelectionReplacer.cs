using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Cida.Platform;

public sealed record ReplacementSnapshot(long Window, int ProcessId, int[] RuntimeId, string Text, string PrefixHash, bool Editable,
    string DocumentText = "", int PrefixLength = 0, long ProcessStarted = 0)
{
    public bool Matches(ReplacementSnapshot? other) => other != null && Editable && other.Editable &&
        Window == other.Window && ProcessId == other.ProcessId && RuntimeId.SequenceEqual(other.RuntimeId) &&
        Text == other.Text && PrefixHash == other.PrefixHash && DocumentText == other.DocumentText && ProcessStarted == other.ProcessStarted;
}

public static class SelectionReplacer
{
    public static ReplacementSnapshot? Capture(nint window)
    {
        if (window == 0 || SelectionReader.ForegroundWindow() != window) return null;
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null || element.Current.IsPassword) return null;
            var root = AutomationElement.FromHandle(window);
            GetWindowThreadProcessId(window, out var pid);
            if (element.Current.ProcessId != pid || root == null) return null;
            var belongs = false;
            for (var current = element; current != null; current = TreeWalker.RawViewWalker.GetParent(current))
                if (Automation.Compare(current, root)) { belongs = true; break; }
            if (!belongs || !element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern) return null;
            var selection = pattern.GetSelection();
            if (selection.Length != 1) return null;
            var range = selection[0]; var text = range.GetText(100001);
            if (string.IsNullOrEmpty(text) || text.Length > 100000) return null;
            var prefix = pattern.DocumentRange.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            var preceding = prefix.GetText(100001);
            if (preceding.Length > 100000) return null;
            var readOnly = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            var editable = readOnly is bool flag && !flag && element.Current.IsEnabled;
            var document = pattern.DocumentRange.GetText(200001);
            if (document.Length > 200000 || preceding.Length + text.Length > document.Length ||
                document.Substring(preceding.Length, text.Length) != text) return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preceding)));
            using var sourceProcess = System.Diagnostics.Process.GetProcessById((int)pid);
            var started = sourceProcess.StartTime.ToUniversalTime().Ticks;
            return new((long)window, (int)pid, element.GetRuntimeId(), text, hash, editable, document, preceding.Length, started);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ElementNotAvailableException) { return null; }
    }
    public static async Task<bool> ReplaceAsync(ReplacementSnapshot original, string replacement, AccessibilityWorker worker, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(replacement) || SelectionReader.ForegroundWindow() != (nint)original.Window) return false;
        var fresh = await worker.QueryAsync(new("replacement", original.Window), cancellation);
        if (!original.Matches(fresh?.Replacement) || cancellation.IsCancellationRequested) return false;
        return await Task.Run(() => PasteValidated(original, replacement, worker, cancellation), cancellation);
    }
    public static bool Verify(ReplacementSnapshot original, string expected)
    {
        try
        {
            if (SelectionReader.ForegroundWindow() != (nint)original.Window) return false;
            var element = AutomationElement.FocusedElement;
            if (element == null || element.Current.ProcessId != original.ProcessId || !element.GetRuntimeId().SequenceEqual(original.RuntimeId) ||
                !element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern) return false;
            return pattern.DocumentRange.GetText(200001) == expected;
        }
        catch { return false; }
    }
    private static bool PasteValidated(ReplacementSnapshot original, string text, AccessibilityWorker worker, CancellationToken cancellation)
    {
        try
        {
            using var before = ClipboardSnapshot.Capture();
            if (before == null) return false;
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0)) return false;
            if (cancellation.IsCancellationRequested || SelectionReader.ForegroundWindow() != (nint)original.Window) return false;
            if (!ClipboardSnapshot.WriteOwnedText(text, before.Sequence, out var sequence))
            { if (sequence != 0) ClipboardSnapshot.RestoreOwned(before, sequence); return false; }
            try
            {
                if (cancellation.IsCancellationRequested || SelectionReader.ForegroundWindow() != (nint)original.Window) return false;
                var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
                if (SendInput(4, inputs, Marshal.SizeOf<INPUT>()) != 4) return false;
                Thread.Sleep(150);
                var expected = original.DocumentText[..original.PrefixLength] + text + original.DocumentText[(original.PrefixLength + original.Text.Length)..];
                var verification = worker.QueryAsync(new("verify-replacement", original.Window) { Snapshot = original, Expected = expected }, cancellation).GetAwaiter().GetResult();
                return verification?.Verified == true;
            }
            finally { ClipboardSnapshot.RestoreOwned(before, sequence); }
        }
        finally { ClipboardSnapshot.ReleaseWindow(); }
    }
    private static INPUT Key(ushort key, bool up) => new() { type = 1, U = new INPUTUNION { keyboard = new() { wVk = key, dwFlags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public HARDWAREINPUT hardware; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint message; public ushort low, high; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
