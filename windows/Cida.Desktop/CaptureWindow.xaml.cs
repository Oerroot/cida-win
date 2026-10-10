using Cida.Core;
using Cida.Platform;
using System.IO;
using System.Windows.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;






namespace Cida.Desktop;

/// <summary>
/// The frozen-screen capture flow: full-screen borderless windows show the frozen shot,
/// the user drags a rectangle, and the crop goes to local OCR. Ported from upstream
/// CaptureOverlay.swift.
/// </summary>
public sealed class CaptureWindow : Window
{
    private readonly AppModel _model;
    private readonly LocalOcr _ocr;
    private readonly System.Windows.Shapes.Path _veil = new() { Fill = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Border _selection = new()
    {
        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xC6, 0xA2)),
        BorderThickness = new Thickness(1.5),
        Background = Brushes.Transparent,
    };
    private ScreenCapturer.CapturedScreen _frozen = null!;
    private System.Windows.Point _start;
    private bool _dragging;
    private readonly Border _hint = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 10, 18, 10), Background = new SolidColorBrush(Color.FromArgb(240, 31, 37, 30)) };

    public CaptureWindow(AppModel model, ScreenCapturer capturer, LocalOcr ocr)
    {
        _model = model;
        _ocr = ocr;
        Style = (Style)FindResource(typeof(Window));
        // Capture before this topmost window is visible, so the image cannot include itself.
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        _frozen = capturer.Capture(screen.Bounds);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        Background = System.Windows.Media.Brushes.Black;

        var canvas = new Canvas();
        canvas.Children.Add(_veil);
        canvas.Children.Add(_selection);
        _hint.Child = new TextBlock { Text = "拖动选择文字区域 · Esc / 右键取消", FontSize = 13, Foreground = Brushes.White };
        canvas.Children.Add(_hint);
        Content = canvas;

        SourceInitialized += (_, _) => WindowPlacement.Cover(this, _frozen.Bounds);

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, e) => { e.Handled = true; Close(); };
        KeyDown += (_, arguments) =>
        {
            if (arguments.Key == Key.Escape)
            {
                arguments.Handled = true;
                Close();
            }
        };
        Deactivated += (_, _) => Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs arguments)
    {
        WindowPlacement.Cover(this, _frozen.Bounds);

        var source = new BitmapImage();
        source.BeginInit();
        source.StreamSource = new MemoryStream(_frozen.Pixels);
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();
        source.Freeze();
        Background = new ImageBrush(source);

        _veil.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
        Canvas.SetLeft(_veil, 0);
        Canvas.SetTop(_veil, 0);
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hint, Math.Max(8, (Width - _hint.DesiredSize.Width) / 2)); Canvas.SetTop(_hint, Math.Max(8, Height - 88));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs arguments)
    {
        _dragging = true;
        _start = arguments.GetPosition(this);
        _selection.Width = 0;
        _selection.Height = 0;
        Canvas.SetLeft(_selection, _start.X);
        Canvas.SetTop(_selection, _start.Y);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs arguments)
    {
        if (!_dragging) return;
        var pointer = arguments.GetPosition(this);
        var position = new Point(Math.Clamp(pointer.X, 0, ActualWidth), Math.Clamp(pointer.Y, 0, ActualHeight));
        var left = Math.Min(_start.X, position.X);
        var top = Math.Min(_start.Y, position.Y);
        _selection.Width = Math.Abs(position.X - _start.X);
        _selection.Height = Math.Abs(position.Y - _start.Y);
        Canvas.SetLeft(_selection, left);
        Canvas.SetTop(_selection, top);
        var cutout = new GeometryGroup { FillRule = FillRule.EvenOdd };
        cutout.Children.Add(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        cutout.Children.Add(new RectangleGeometry(new Rect(left, top, _selection.Width, _selection.Height)));
        _veil.Data = cutout;
    }

    private async void OnMouseUp(object sender, MouseButtonEventArgs arguments)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_selection.Width < 4 || _selection.Height < 4)
        {
            _veil.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
            return;
        }

        var pixels = CaptureCoordinates.Crop(Canvas.GetLeft(_selection), Canvas.GetTop(_selection),
            _selection.Width, _selection.Height, ActualWidth, ActualHeight, _frozen.Width, _frozen.Height);
        if (pixels.Width == 0 || pixels.Height == 0) { Close(); return; }
        var cropWidth = pixels.Width;
        var cropHeight = pixels.Height;
        var crop = CropPng(_frozen, pixels.X, pixels.Y, cropWidth, cropHeight);
        Close();

        await _model.RecognizeCaptureAsync(crop, cropWidth, cropHeight);
    }

    private static byte[] CropPng(ScreenCapturer.CapturedScreen frozen, int x, int y, int width, int height)
    {
        using var source = new System.Drawing.Bitmap(new MemoryStream(frozen.Pixels));
        using var crop = source.Clone(
            new System.Drawing.Rectangle(x, y, width, height), source.PixelFormat);
        using var memory = new MemoryStream();
        crop.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
        return memory.ToArray();
    }
}
