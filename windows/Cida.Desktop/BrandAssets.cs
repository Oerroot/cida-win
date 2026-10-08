using System;
using System.Runtime.InteropServices;

namespace Cida.Desktop;

public static class BrandAssets
{
    public static System.Drawing.Icon LoadTrayIcon()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == 0 ? 96 : Math.Max(96, GetDpiForWindow(taskbar));
        var size = GetSystemMetricsForDpi(49, (uint)dpi); // SM_CXSMICON
        if (size < 16) size = 16;
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Cida;component/Assets/Brand/Cida.ico")).Stream;
        using var icon = new System.Drawing.Icon(stream, size, size);
        return (System.Drawing.Icon)icon.Clone();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
