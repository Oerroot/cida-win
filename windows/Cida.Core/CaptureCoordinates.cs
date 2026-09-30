namespace Cida.Core;

/// <summary>Maps a selection in the displayed image to a clamped crop in native pixels.</summary>
public static class CaptureCoordinates
{
    public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

    public static PixelBounds Crop(double x, double y, double width, double height,
        double displayedWidth, double displayedHeight, int pixelWidth, int pixelHeight)
    {
        if (displayedWidth <= 0 || displayedHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayedWidth));
        var scaleX = pixelWidth / displayedWidth;
        var scaleY = pixelHeight / displayedHeight;
        var left = (int)Math.Clamp(Math.Floor(x * scaleX), 0, pixelWidth);
        var top = (int)Math.Clamp(Math.Floor(y * scaleY), 0, pixelHeight);
        var right = (int)Math.Clamp(Math.Ceiling((x + width) * scaleX), left, pixelWidth);
        var bottom = (int)Math.Clamp(Math.Ceiling((y + height) * scaleY), top, pixelHeight);
        return new(left, top, right - left, bottom - top);
    }
}
