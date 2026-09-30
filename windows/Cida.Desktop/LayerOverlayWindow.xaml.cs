using System.Runtime.InteropServices;
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cida.Desktop;

/// <summary>
/// The click-through overlay that draws a translation over the paragraph it replaces.
/// Ported from upstream TranslationLayerOverlay.swift: mouse events pass to the application
/// below, the translation is laid out to fit the paragraph's bounds, and pressing the layer
/// shortcut again removes it.
/// </summary>
public sealed class LayerOverlayWindow : Window
{
    private readonly System.Windows.Controls.TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13.5,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    public LayerOverlayWindow(string translation, System.Drawing.RectangleF bounds)
    {
        Title = "辞达 · 原处翻译";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // The "paper" behind the translation, as the upstream layer draws it.
        var paper = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 248, 247, 244)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 0x1F, 0x1D, 0x1A)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4, 6, 4),
            Child = _text,
        };
        _text.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x1D, 0x1A));
        _text.Text = translation;
        Content = paper;

        // Place over the paragraph; grow past its width rather than clip the meaning.
        MaxHeight = 320;
        SizeToContent = SizeToContent.Height;

        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            MoveToBounds(bounds);
        };
    }

    public void MoveToBounds(System.Drawing.RectangleF bounds) => WindowPlacement.Overlay(this, bounds);
    public void SetText(string text) => _text.Text = text;

    /// <summary>Appends a streamed piece of the translation.</summary>
    public void Append(string piece) => _text.Text += piece;

    private void MakeClickThrough()
    {
        var helper = new WindowInteropHelper(this);
        var style = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE,
            style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
