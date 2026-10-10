using System;
using System.Windows.Media;

namespace Cida.Desktop;

/// <summary>Paints interactive chrome without a seam between the rounded fill and outline.</summary>
public sealed class ControlSurface : Border
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        var thickness = BorderThickness;
        var radius = CornerRadius;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (thickness.Left != thickness.Top || thickness.Left != thickness.Right || thickness.Left != thickness.Bottom ||
            radius.TopLeft != radius.TopRight || radius.TopLeft != radius.BottomRight || radius.TopLeft != radius.BottomLeft ||
            dpi.DpiScaleX != dpi.DpiScaleY)
        {
            base.OnRender(drawingContext);
            return;
        }

        // Match Border's rounded layout thickness. The stroke is centered half its width
        // inside the bounds; its fill extends underneath it, so separately antialiased
        // inner edges cannot expose the parent's background at the corners.
        var width = UseLayoutRounding ? Math.Round(thickness.Left * dpi.DpiScaleX) / dpi.DpiScaleX : thickness.Left;
        var inset = width / 2;
        if (RenderSize.Width <= width || RenderSize.Height <= width) return;
        Pen? pen = null;
        if (width > 0 && BorderBrush != null)
        {
            pen = new Pen(BorderBrush, width);
            if (pen.CanFreeze) pen.Freeze();
        }
        var outerRadius = radius.TopLeft == 0 ? 0 : radius.TopLeft + inset;
        drawingContext.DrawRoundedRectangle(Background, null, new Rect(RenderSize), outerRadius, outerRadius);
        // A same-color outline needs no second coverage pass (primary buttons).
        var sameColor = Background is SolidColorBrush fill && BorderBrush is SolidColorBrush stroke &&
            fill.Color == stroke.Color && fill.Opacity == stroke.Opacity;
        if (pen != null && !sameColor)
            drawingContext.DrawRoundedRectangle(null, pen,
                new Rect(inset, inset, RenderSize.Width - width, RenderSize.Height - width),
                radius.TopLeft, radius.TopLeft);
    }
}
