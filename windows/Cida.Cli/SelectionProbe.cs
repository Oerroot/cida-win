using System.Runtime.InteropServices;
using System.Text;
using Cida.Platform;

namespace Cida.Cli;

/// <summary>
/// The P0 probe: reports, for every visible top-level window, how the selection reader
/// would reach its text — UI Automation TextPattern or the copy fallback — as a capability
/// matrix. Select text in the applications you care about, run <c>cida probe</c>, and read
/// the table.
/// </summary>
public static class SelectionProbe
{
    public static void Run()
    {
        Console.WriteLine("辞达选区读取能力矩阵探针");
        Console.WriteLine("在目标应用里选中一段文字，然后按回车检查可见窗口；Ctrl+C 退出。\n");
        var reader = new SelectionReader();
        while (true)
        {
            Console.Write("按回车检查当前可见窗口（Ctrl+C 退出）…");
            Console.ReadLine();
            var rows = new List<(string Window, string Kind, string Strategy, string Excerpt)>();
            foreach (var (handle, title) in VisibleWindows())
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                var result = reader.ReadFromAutomation(handle);
                rows.Add((title, result.Kind.ToString(), result.Strategy,
                    result.Text is { Length: > 0 } text ? text[..Math.Min(30, text.Length)] : ""));
            }
            Console.WriteLine();
            Console.WriteLine($"{"窗口".PadRight(36)}{"结果".PadRight(14)}{"策略".PadRight(12)}摘要");
            foreach (var row in rows.Where(row => row.Kind != "Unreadable"))
            {
                Console.WriteLine(
                    $"{Truncate(row.Window, 34).PadRight(36)}{row.Kind.PadRight(14)}{row.Strategy.PadRight(12)}{row.Excerpt}");
            }
            var unreadable = rows.Count(row => row.Kind == "Unreadable");
            Console.WriteLine($"\n（另有 {unreadable} 个窗口无法通过自动化读取；实际使用时会走复制兜底）\n");
        }
    }

    private static string Truncate(string text, int width)
    {
        var columns = 0;
        foreach (var ch in text)
        {
            columns += ch >= 0x2E80 ? 2 : 1;
        }
        return columns <= width ? text : text[..width];
    }

    private static List<(nint Handle, string Title)> VisibleWindows()
    {
        var windows = new List<(nint, string)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var length = GetWindowTextLength(handle);
            if (length == 0) return true;
            var builder = new StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            windows.Add((handle, builder.ToString()));
            return true;
        }, nint.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);
}
