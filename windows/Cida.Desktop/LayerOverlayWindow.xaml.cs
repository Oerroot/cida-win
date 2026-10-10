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
        Style = (Style)FindResource(typeof(Window));
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
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0.5, 2, 0.5),
            Child = _text,
            ClipToBounds = true,
        };
        paper.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Paper");
        paper.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "PanelBorder");
        RenderOptions.SetClearTypeHint(paper, ClearTypeHint.Enabled);
        _text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Ink");
        _text.Text = translation;
        Content = paper;

        // Keep the source bounds; oversized translations are retained in the main panel.
        SizeToContent = SizeToContent.Manual;

        SourceInitialized += (_, _) =>
        {
            MakeClickThrough();
            MoveToBounds(bounds);
        };
    }

    public bool Fits { get; private set; } = true;
    public string FullText => _text.Text;
    public void MoveToBounds(System.Drawing.RectangleF bounds) { WindowPlacement.Overlay(this, bounds); Fit(); }
    public void SetText(string text) { _text.Text = text; Fit(); }
    private void Fit()
    {
        var width = Math.Max(1, Width - 5); var height = Math.Max(1, Height - 2);
        Fits = false;
        for (var size = 14d; size >= 11; size -= 0.5)
        {
            _text.FontSize = size; _text.Measure(new Size(width, double.PositiveInfinity));
            if (_text.DesiredSize.Height <= height) { Fits = true; break; }
        }
        _text.Visibility = Fits ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Appends a streamed piece of the translation.</summary>
    public void Append(string piece) => SetText(_text.Text + piece);

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
