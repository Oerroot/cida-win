using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Cida.Platform;

public sealed class WindowParagraphReader
{
    public sealed record Paragraph(int[] RuntimeId, string Text, System.Drawing.RectangleF Bounds, int StartOffset = -1, string? FullText = null);
    public static nint ForegroundWindow() => SelectionReader.ForegroundWindow();
    public static string Key(int[] runtimeId) => string.Join(".", runtimeId);
    public IReadOnlyList<Paragraph> ReadVisibleParagraphs(nint window)
    {
        var result = new List<Paragraph>();
        if (window == 0) return result;
        try
        {
            var root = AutomationElement.FromHandle(window);
            if (root == null) return result;
            var viewport = root.Current.BoundingRectangle;
            var elements = root.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            var seen = new HashSet<string>();
            foreach (AutomationElement element in elements)
            {
                if (result.Count >= 128) break;
                try
                {
                    if (!AccessibleParagraphs.Eligible(element) || !element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern) continue;
                    var elementBounds = element.Current.BoundingRectangle; elementBounds.Intersect(viewport);
                    if (elementBounds.IsEmpty) continue;
                    var ordinal = 0;
                    foreach (var visible in pattern.GetVisibleRanges())
                    {
                        var range = visible.Clone();
                        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                        range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                        for (var count = 0; count < 128 && result.Count < 128; count++)
                        {
                            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, visible, TextPatternRangeEndpoint.End) >= 0) break;
                            var clipped = range.Clone();
                            if (clipped.CompareEndpoints(TextPatternRangeEndpoint.Start, visible, TextPatternRangeEndpoint.Start) < 0)
                                clipped.MoveEndpointByRange(TextPatternRangeEndpoint.Start, visible, TextPatternRangeEndpoint.Start);
                            if (clipped.CompareEndpoints(TextPatternRangeEndpoint.End, visible, TextPatternRangeEndpoint.End) > 0)
                                clipped.MoveEndpointByRange(TextPatternRangeEndpoint.End, visible, TextPatternRangeEndpoint.End);
                            var text = clipped.GetText(8193).Trim();
                            var bounds = AccessibleParagraphs.Bounds(clipped, elementBounds);
                            var identity = text + "|" + bounds?.X.ToString("F0") + "|" + bounds?.Y.ToString("F0");
                            if (text.Length is > 0 and <= 8192 && bounds is { } rect && !AccessibleParagraphs.IsEditable(clipped) && !AccessibleParagraphs.IsCode(clipped, text) && seen.Add(identity))
                            {
                                var prefix = pattern.DocumentRange.Clone(); prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                                var preceding = prefix.GetText(200001);
                                result.Add(new(element.GetRuntimeId().Append(ordinal).ToArray(), text, rect, preceding.Length <= 200000 ? preceding.Length : -1, range.GetText(8193).Trim()));
                            }
                            ordinal++;
                            var start = range.Clone();
                            if (range.Move(TextUnit.Paragraph, 1) == 0 || range.Compare(start)) break;
                        }
                    }
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ElementNotAvailableException) { }
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ElementNotAvailableException) { }
        return result.OrderBy(p => p.Bounds.Top).ThenBy(p => p.Bounds.Left).ToList();
    }
}
