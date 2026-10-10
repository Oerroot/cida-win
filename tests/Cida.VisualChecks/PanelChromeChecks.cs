using Cida.Desktop;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Point = System.Windows.Point;

namespace Cida.VisualChecks;

internal static class PanelChromeChecks
{
    public static void Run(PanelWindow panel, string output)
    {
        panel.UpdateLayout();
        var header = (FrameworkElement)panel.FindName("CaptionBar");
        var title = (FrameworkElement)panel.FindName("TitleBar");
        var settings = (FrameworkElement)panel.FindName("SettingsButton");
        var close = (FrameworkElement)panel.FindName("HideButton");
        var bottom = header.TranslatePoint(new Point(0, header.ActualHeight), panel).Y;
        var caption = WindowChrome.GetWindowChrome(panel).CaptionHeight;
        if (Math.Abs(caption - bottom) > .01)
            throw new InvalidOperationException("Native caption does not cover the full measured header.");
        var points = new List<(string Name, Point Position, int Expected)>
        {
            ("top-edge", new Point(panel.ActualWidth / 2, 1), 2),
            ("top-padding", new Point(panel.ActualWidth / 2, 6), 2),
            ("left-padding", new Point(12, bottom - 18), 2),
            ("right-padding", new Point(panel.ActualWidth - 10, bottom - 18), 2),
            ("title-blank", new Point((title.TranslatePoint(new Point(75, 0), panel).X + settings.TranslatePoint(new Point(), panel).X) / 2, bottom - title.ActualHeight / 2), 2),
            ("icon", Center((FrameworkElement)panel.FindName("BrandIcon")), 2),
            ("brand-text", title.TranslatePoint(new Point(48, title.ActualHeight / 2), panel), 2),
            ("settings", Center(settings), 1),
            ("close", Center(close), 1),
            ("settings-label", settings.TranslatePoint(new Point(settings.ActualWidth - 20, settings.ActualHeight / 2), panel), 1),
            ("between-buttons", settings.TranslatePoint(new Point(settings.ActualWidth + 3, settings.ActualHeight / 2), panel), 2),
            ("below-caption", new Point(panel.ActualWidth / 2, bottom + 4), 1),
            ("composer", Center((FrameworkElement)panel.FindName("Composer")), 1),
            ("execute", Center((FrameworkElement)panel.FindName("ExecuteButton")), 1),
            ("result", Center((FrameworkElement)panel.FindName("ResultView")), 1)
        };
        var hwnd = new WindowInteropHelper(panel).Handle;
        var results = points.Select(p => {
            var screen = panel.PointToScreen(p.Position);
            // WM_NCHITTEST uses signed 16-bit physical screen coordinates, including negative monitors.
            var packed = unchecked((int)((uint)(ushort)(short)Math.Round(screen.X) |
                ((uint)(ushort)(short)Math.Round(screen.Y) << 16)));
            var actual = SendMessage(hwnd, 0x84, 0, (nint)packed).ToInt32();
            return new { name=p.Name, x=p.Position.X, y=p.Position.Y, expected=p.Expected, actual };
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { width=panel.ActualWidth, captionHeight=caption,
            dpi=VisualTreeHelper.GetDpi(panel).DpiScaleX * 96, results }, new JsonSerializerOptions { WriteIndented=true }));
        var failed = results.Where(r => r.actual != r.expected).ToArray();
        if (failed.Length > 0)
            throw new InvalidOperationException("Native caption hit-test failures: " + string.Join(", ", failed.Select(r => $"{r.name}: {r.actual}, expected {r.expected}")));
        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), panel);
    }

    [DllImport("user32.dll", EntryPoint="SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
