using System.Runtime.InteropServices;

namespace Cida.Platform;

/// <summary>
/// Enumerates the visible text paragraphs of a window and follows them as the window
/// scrolls. Windows port of the upstream translation layer's reading side
/// (TranslationLayerAccessibility + the Controller's 20Hz follow): the initial read walks
/// the UIA tree for TextPattern elements; re-reads go straight to the remembered element
/// runtime ids and read their current bounds, so a scrolled paragraph is re-located
/// without walking the tree again.
/// </summary>
public sealed class WindowParagraphReader
{
    /// <summary>One paragraph: its text, its element id, and its current bounds.</summary>
    public sealed record Paragraph(
        int[] RuntimeId,
        string Text,
        System.Drawing.RectangleF Bounds);

    private nint _window;
    private Dictionary<string, System.Windows.Automation.AutomationElement> _elements = new();

    /// <summary>The window the layer translates; captured when the layer opens.</summary>
    public static nint ForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// The initial read: visible paragraphs of the window, top-to-bottom. Elements whose
    /// text is empty or whose bounds are off-window are skipped.
    /// </summary>
    public IReadOnlyList<Paragraph> ReadVisibleParagraphs(nint window)
    {
        _window = window;
        var remembered = new Dictionary<string, System.Windows.Automation.AutomationElement>();
        var paragraphs = new List<Paragraph>();
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(window);
            if (root == null) return paragraphs;
            var elements = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.IsTextPatternAvailableProperty, true));
            var windowBounds = root.Current.BoundingRectangle;
            foreach (System.Windows.Automation.AutomationElement element in elements)
            {
                try
                {
                    if (element.Current.IsPassword) continue;
                    if (element.GetCurrentPattern(
                            System.Windows.Automation.TextPattern.Pattern)
                        is not System.Windows.Automation.TextPattern textPattern)
                    {
                        continue;
                    }
                    var text = textPattern.DocumentRange.GetText(int.MaxValue)?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    var bounds = element.Current.BoundingRectangle;
                    if (bounds.IsEmpty) continue;
                    // Off-window elements are not visible; keep the ones the window shows.
                    if (bounds.Height <= 0 || bounds.Width <= 0) continue;
                    if (windowBounds.Width > 0
                        && (bounds.Bottom < windowBounds.Top || bounds.Top > windowBounds.Bottom))
                    {
                        continue;
                    }
                    var runtimeId = element.GetRuntimeId();
                    remembered[Key(runtimeId)] = element;
                    paragraphs.Add(new Paragraph(
                        runtimeId,
                        text,
                        new System.Drawing.RectangleF(
                            (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height)));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // The element vanished mid-walk; skip it.
                }
                catch (System.InvalidOperationException)
                {
                    // Element not available; skip it.
                }
                catch (System.Windows.Automation.ElementNotAvailableException)
                {
                    // The element disappeared between discovery and read; skip it.
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        catch (System.InvalidOperationException)
        {
        }
        // Publish a completed map so the UI poll never enumerates a map being populated.
        Interlocked.Exchange(ref _elements, remembered);
        return paragraphs.OrderBy(paragraph => paragraph.Bounds.Top)
            .ThenBy(paragraph => paragraph.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// The follow-up read after a scroll: for each remembered element, its current bounds.
    /// Missing elements (the paragraph scrolled out of the virtualized tree) are absent
    /// from the result; new paragraphs need a full re-read.
    /// </summary>
    public IReadOnlyList<Paragraph> ReReadBounds()
    {
        var paragraphs = new List<Paragraph>();
        foreach (var (key, element) in _elements)
        {
            try
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty) continue;
                if (element.GetCurrentPattern(
                        System.Windows.Automation.TextPattern.Pattern)
                    is not System.Windows.Automation.TextPattern textPattern)
                {
                    continue;
                }
                var text = textPattern.DocumentRange.GetText(int.MaxValue)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                paragraphs.Add(new Paragraph(
                    element.GetRuntimeId(),
                    text,
                    new System.Drawing.RectangleF(
                        (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height)));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Gone; the caller's full re-read will pick up what replaced it.
            }
            catch (System.InvalidOperationException)
            {
            }
        }
        return paragraphs.OrderBy(paragraph => paragraph.Bounds.Top)
            .ThenBy(paragraph => paragraph.Bounds.Left)
            .ToList();
    }

    public static string Key(int[] runtimeId) => string.Join(".", runtimeId);

    /// <summary>Whether the tracked window is still the foreground one.</summary>
    public bool IsWindowStillFront(nint window) => GetForegroundWindow() == (window != 0 ? window : _window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
