using System.Runtime.InteropServices;

namespace Cida.Platform;

/// <summary>
/// Reads the paragraph of text under the pointer and places a translation over it: the
/// Windows port of the upstream translation layer's pointer flow (TranslationLayer*). The
/// overlay window is click-through (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE),
/// and the paragraph bounds come from UI Automation.
/// </summary>
public sealed class TranslationLayer
{
    public sealed record LayerParagraph(string Text, System.Drawing.RectangleF Bounds);

    /// <summary>The paragraph under the pointer, read through UI Automation.</summary>
    public LayerParagraph? ParagraphUnderCursor()
    {
        GetCursorPos(out var point);
        try
        {
            var element = System.Windows.Automation.AutomationElement.FromPoint(
                new System.Windows.Point(point.X, point.Y));
            if (element == null) return null;
            if (element.Current.IsPassword) return null;
            if (!element.TryGetCurrentPattern(
                    System.Windows.Automation.TextPattern.Pattern, out var patternObject)
                || patternObject is not System.Windows.Automation.TextPattern textPattern)
            {
                return null;
            }
            // The full text of the element gives the paragraph; the bounding rectangle places it.
            var text = textPattern.DocumentRange.GetText(int.MaxValue)?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            return new LayerParagraph(
                text,
                new System.Drawing.RectangleF(
                    (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        catch (System.InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
