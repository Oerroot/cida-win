using System;
using System.Linq;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace Cida.Desktop;

internal static class ResultDocument
{
    public static void Render(FlowDocument document, string text)
    {
        document.Blocks.Clear();
        var code = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            var paragraph = new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, line.Length == 0 ? 10 : 2) };
            if (line.Length == 0) { paragraph.FontSize = 1; paragraph.LineHeight = 6; paragraph.Margin = new Thickness(0, 0, 0, 6); }
            if (code)
            {
                paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas"); paragraph.FontSize = 14;
                paragraph.LineHeight = 23; paragraph.Padding = new Thickness(10, 3, 10, 3);
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft");
            }
            document.Blocks.Add(paragraph);
        }
    }
    public static BitmapSource Image(string text, string title)
    {
        // Render the entire result independently of the viewport. Refuse oversized images explicitly.
        var document = new FlowDocument { PageWidth = 744, ColumnWidth = double.PositiveInfinity, PagePadding = new Thickness(28), FontSize = 18, LineHeight = 30 };
        document.SetResourceReference(TextElement.FontFamilyProperty, "ReadingFont");
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        Render(document, text);
        document.Blocks.InsertBefore(document.Blocks.FirstBlock, new Paragraph(new Run("辞达 · " + title)) { FontSize = 13, Margin = new Thickness(0, 0, 0, 20), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") });
        var viewer = new FlowDocumentScrollViewer { Document = document, Width = 744, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewer.SetResourceReference(Control.BackgroundProperty, "Paper");
        viewer.Measure(new Size(744, double.PositiveInfinity));
        var height = Math.Ceiling(viewer.DesiredSize.Height);
        if (height > 12000) throw new InvalidOperationException("结果太长，无法复制为单张图片，请复制文字。");
        viewer.Arrange(new Rect(0, 0, 744, Math.Max(120, height))); viewer.UpdateLayout();
        var image = new RenderTargetBitmap(744, (int)Math.Max(120, height), 96, 96, PixelFormats.Pbgra32);
        image.Render(viewer); image.Freeze(); return image;
    }
}
