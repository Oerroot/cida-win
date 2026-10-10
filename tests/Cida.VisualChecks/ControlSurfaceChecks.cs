using Cida.Desktop;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Size = System.Windows.Size;

namespace Cida.VisualChecks;

internal static class ControlSurfaceChecks
{
    public static void Run(string directory)
    {
        var metrics = new List<object>();
        foreach (var dark in new[] { false, true })
        {
            ThemeService.Apply(dark);
            var background = (SolidColorBrush)Application.Current.FindResource("PanelBackground");
            var fill = (SolidColorBrush)Application.Current.FindResource("Accent");
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                int Render(bool legacy)
                {
                    Border surface = legacy ? new Border() : new ControlSurface();
                    surface.Background = fill; surface.BorderBrush = fill; surface.BorderThickness = new Thickness(1);
                    surface.CornerRadius = new CornerRadius(6); surface.Width = 100; surface.Height = 34;
                    surface.Margin = new Thickness(16);
                    var container = new Grid { Width = 132, Height = 66, Background = background,
                        UseLayoutRounding = true, SnapsToDevicePixels = true };
                    container.Children.Add(surface);
                    container.Measure(new Size(132, 66)); container.Arrange(new Rect(0, 0, 132, 66)); container.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)(132 * scale), (int)(66 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(container);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var name = $"{(dark ? "dark" : "light")}-surface-{(legacy ? "legacy" : "fixed")}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    using (var stream = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(stream);
                    var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight];
                    bitmap.CopyPixels(pixels, stride, 0);
                    int Distance(int x, int y)
                    {
                        var offset = y * stride + x * 4;
                        return Math.Abs(pixels[offset] - fill.Color.B) + Math.Abs(pixels[offset + 1] - fill.Color.G) + Math.Abs(pixels[offset + 2] - fill.Color.R);
                    }
                    // A solid corner must approach the fill monotonically. The legacy
                    // two-pass border produces a second band of parent color inside it.
                    var origin = (int)(16 * scale); var corner = (int)(10 * scale); var regressions = 0;
                    for (var y = origin; y < origin + corner; y++)
                        for (var x = origin + 1; x < origin + corner; x++)
                            if (Distance(x, y) > Distance(x - 1, y) + 3) regressions++;
                    return regressions;
                }
                var oldBands = Render(true); var newBands = Render(false);
                metrics.Add(new { dark, scale, legacyCornerBands = oldBands, fixedCornerBands = newBands });
                if (newBands != 0 || (scale == 1 && oldBands == 0))
                    throw new InvalidOperationException($"Rounded surface regression at {scale}: legacy={oldBands}, fixed={newBands}.");
            }
        }
        File.WriteAllText(Path.Combine(directory, "control-surface-metrics.json"), JsonSerializer.Serialize(metrics));
    }
}
