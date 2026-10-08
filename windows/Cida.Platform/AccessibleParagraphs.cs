using System.Drawing;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Cida.Platform;

internal static class AccessibleParagraphs
{
    public static bool Eligible(AutomationElement element) => !element.Current.IsPassword && !element.Current.IsOffscreen &&
        element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.Button &&
        element.Current.ControlType != ControlType.Hyperlink && element.Current.ControlType != ControlType.ComboBox;

    public static RectangleF? Bounds(TextPatternRange range, System.Windows.Rect viewport, System.Windows.Point? pointer = null)
    {
        var coordinates = range.GetBoundingRectangles();
        RectangleF? union = null; var contains = pointer == null;
        foreach (var coordinate in coordinates)
        {
            var rect = coordinate;
            rect.Intersect(viewport);
            if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2) continue;
            if (pointer is { } point && rect.Contains(point)) contains = true;
            var next = new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
            union = union == null ? next : RectangleF.Union(union.Value, next);
        }
        // A provider expanding Paragraph to an entire document is not a valid overlay target.
        return contains && union is { Height: <= 320, Width: > 8 } ? union : null;
    }
    public static bool IsCode(TextPatternRange range, string text)
    {
        var font = range.GetAttributeValue(TextPattern.FontNameAttribute) as string ?? "";
        return new[] { "mono", "consolas", "courier", "code" }.Any(name => font.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
            text.TrimStart().StartsWith("```") || text.Count(ch => ch is '{' or '}' or ';') > 3;
    }
    public static bool IsEditable(TextPatternRange range) => range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
}
