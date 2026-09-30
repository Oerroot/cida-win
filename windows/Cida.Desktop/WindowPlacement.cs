using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cida.Desktop;

/// <summary>Position HWNDs in physical desktop coordinates; WPF content dimensions stay in DIPs.</summary>
internal static class WindowPlacement
{
    public static void Move(Window window, double x, double y)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, 0, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
            0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    public static double Scale(Window window) => Math.Max(96, GetDpiForWindow(new WindowInteropHelper(window).Handle)) / 96d;

    public static void Cover(Window window, System.Drawing.Rectangle bounds)
    {
        Move(window, bounds.Left, bounds.Top);
        var scale = Scale(window);
        window.Width = bounds.Width / scale;
        window.Height = bounds.Height / scale;
        SetWindowPos(new WindowInteropHelper(window).Handle, 0, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0x0004 | 0x0010);
    }

    public static void Overlay(Window window, System.Drawing.RectangleF bounds)
    {
        Move(window, bounds.Left - 2, bounds.Top - 2);
        window.Width = Math.Max(bounds.Width + 4, 200) / Scale(window);
    }

    public static void NearCursor(Window window)
    {
        var point = System.Windows.Forms.Cursor.Position;
        var working = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        Move(window, point.X, point.Y); // Establish the target monitor's DPI first.
        var scale = Scale(window);
        var width = window.Width * scale;
        var height = (window.ActualHeight > 0 ? window.ActualHeight : 240) * scale;
        Move(window, Math.Max(working.Left + 8, Math.Min(point.X - width / 2, working.Right - width - 8)),
            Math.Max(working.Top + 8, Math.Min(point.Y + 20, working.Bottom - height - 8)));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
