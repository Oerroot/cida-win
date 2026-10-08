using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Windows.Media;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using Pen = System.Drawing.Pen;
using Image = System.Drawing.Image;

namespace Cida.Desktop;

/// <summary>Native tray focus/dismissal behavior, with the product's shared theme.</summary>
public static class TrayMenu
{
    public static ContextMenuStrip Create(AppModel model)
    {
        var ownedFont = new Font("Microsoft YaHei UI", 10);
        var images = new Dictionary<ToolStripItem, Image>();
        var menu = new ContextMenuStrip
        {
            Font = ownedFont, Padding = new Padding(5),
            ShowImageMargin = true, ShowCheckMargin = false, MinimumSize = new Size(272, 0),
            ImageScalingSize = new Size(20, 20),
            Renderer = new Renderer(),
        };
        ToolStripMenuItem Add(string text, string symbol, Action action)
        {
            var item = new ToolStripMenuItem(text, Symbol(symbol), (_, _) => action())
            { AutoSize = false, Size = new Size(252, 36), Padding = new Padding(10, 0, 10, 0) };
            images[item] = item.Image!;
            menu.Items.Add(item); return item;
        }
        Add("显示面板", "\uE8A7", model.OpenPanel);
        var capture = Add("截图翻译", "\uE722", model.StartCapture);
        menu.Items.Add(new ToolStripSeparator());
        var settings = Add("设置", "\uE713", model.ShowSettings);
        settings.ShortcutKeyDisplayString = "Ctrl+,";
        menu.Items.Add(new ToolStripSeparator());
        Add("退出辞达", "\uE8BB", model.Exit);
        menu.Opening += (_, _) =>
        {
            var scale = menu.DeviceDpi / 96d;
            var imageSize = (int)Math.Round(20 * scale);
            menu.MinimumSize = new Size((int)Math.Round(272 * scale), 0);
            menu.ImageScalingSize = new Size(imageSize, imageSize);
            var shortcut = model.Settings.CaptureShortcut;
            capture.ShortcutKeyDisplayString = shortcut is { } value
                ? value.DisplayText.Replace(" " + value.KeyDisplayName, "+" + value.KeyDisplayName.ToUpperInvariant()).Replace(" ", "+") : "";
            menu.BackColor = Theme("PanelBackground");
            menu.ForeColor = Theme("Ink");
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = Theme("Ink");
                if (item is ToolStripMenuItem) item.Size = new Size((int)Math.Round(252 * scale), (int)Math.Round(36 * scale));
            }
            foreach (ToolStripItem item in menu.Items)
                if (item.Tag is string glyph) { var old = images[item]; item.Image = Symbol(glyph, menu.DeviceDpi); images[item] = item.Image; old.Dispose(); }
        };
        // Each menu owns its generated images and font.
        foreach (ToolStripItem item in menu.Items)
            if (item.Image != null) item.Tag = item.Text switch
            { "显示面板" => "\uE8A7", "截图翻译" => "\uE722", "设置" => "\uE713", _ => "\uE8BB" };
        menu.Disposed += (_, _) => { foreach (var image in images.Values) image.Dispose(); ownedFont.Dispose(); };
        return menu;
    }
    private static Color Theme(string name)
    {
        var color = ((SolidColorBrush)System.Windows.Application.Current.FindResource(name)).Color;
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }
    private static Bitmap Symbol(string glyph, int dpi = 96)
    {
        var scale = dpi / 96f;
        var size = (int)Math.Round(20 * scale);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe MDL2 Assets", 16 * scale, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Theme("InkSecondary"));
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() { RoundedEdges = false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme("PanelBackground"));
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Theme("ControlBorder"));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            using var brush = new SolidBrush(Theme("ControlHover"));
            e.Graphics.FillRectangle(brush, new Rectangle(0, 0, e.Item.Width, e.Item.Height));
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Theme("PanelBorder"));
            e.Graphics.DrawLine(pen, 9, e.Item.Height / 2, e.Item.Width - 9, e.Item.Height / 2);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var shortcut = e.Item is ToolStripMenuItem item && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString;
            var scale = (e.Item.Owner?.DeviceDpi ?? 96) / 96d;
            int Px(int dip) => (int)Math.Round(dip * scale);
            var bounds = shortcut ? new Rectangle(e.Item.Width - Px(110), 0, Px(100), e.Item.Height) : new Rectangle(Px(36), 0, Math.Max(1,e.Item.Width - Px(132)), e.Item.Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                (shortcut ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, Theme(shortcut || !e.Item.Enabled ? "InkSecondary" : "Ink"), flags);
        }
    }
}
