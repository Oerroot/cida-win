using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Cida.Platform;

public sealed class TranslationLayer
{
    public sealed record LayerParagraph(string Text, System.Drawing.RectangleF Bounds, int[]? RuntimeId = null, int StartOffset = -1);
    public LayerParagraph? ParagraphUnderCursor()
    { var point = System.Windows.Forms.Cursor.Position; return ParagraphAtPoint(point.X, point.Y); }
    public LayerParagraph? ParagraphAtPoint(int x, int y)
    {
        var point = new System.Windows.Point(x, y);
        try
        {
            var element = AutomationElement.FromPoint(point);
            for (var depth = 0; element != null && depth < 12; depth++, element = TreeWalker.RawViewWalker.GetParent(element))
            {
                if (!AccessibleParagraphs.Eligible(element)) return null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern) continue;
                var range = pattern.RangeFromPoint(point);
                range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                var text = range.GetText(8193).Trim();
                if (text.Length == 0 || text.Length > 8192 || AccessibleParagraphs.IsEditable(range) || AccessibleParagraphs.IsCode(range, text)) return null;
                var bounds = AccessibleParagraphs.Bounds(range, element.Current.BoundingRectangle, point);
                if (bounds == null)
                {
                    range = pattern.RangeFromPoint(point); range.ExpandToEnclosingUnit(TextUnit.Line);
                    text = range.GetText(8193).Trim(); bounds = AccessibleParagraphs.Bounds(range, element.Current.BoundingRectangle, point);
                }
                var prefix = pattern.DocumentRange.Clone(); prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                var preceding = prefix.GetText(200001);
                return bounds is { } rect && text.Length is > 0 and <= 8192 && preceding.Length <= 200000
                    ? new(text, rect, element.GetRuntimeId(), preceding.Length) : null;
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ElementNotAvailableException) { }
        return null;
    }
}
