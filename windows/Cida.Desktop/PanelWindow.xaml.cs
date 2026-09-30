using Cida.Core;
using Cida.Platform;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;




namespace Cida.Desktop;

/// <summary>
/// The floating panel: shows the frontmost selection, streams the reply, and switches
/// between translation and improvement. The window does not take focus from the source
/// application until the composer is clicked (WS_EX_NOACTIVATE until then).
/// </summary>
public sealed class PanelWindow : Window
{
    /// <summary>What the selection reader produced for one show.</summary>
    public sealed record Submission(string Text, SelectionReader Reader);

    private readonly AppModel _model;
    private readonly System.Windows.Controls.TextBox _composer = new();
    private readonly System.Windows.Controls.TextBlock _result = new();
    private readonly System.Windows.Controls.TextBlock _note = new();
    private readonly System.Windows.Controls.Button _modeSwitch = new();
    private ProcessingMode _mode = ProcessingMode.Translate;
    private CancellationTokenSource? _cancellation;
    private ResultPhase _phase = ResultPhase.Completed;

    public PanelWindow(AppModel model)
    {
        _model = model;
        Title = "辞达";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = FindResource("PanelBackground") as Brush;
        BorderBrush = FindResource("PanelBorder") as Brush;
        BorderThickness = new Thickness(1);
        FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./#Microsoft YaHei UI");

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };

        var header = new System.Windows.Controls.Grid();
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        var title = new System.Windows.Controls.TextBlock
        {
            Text = "翻译",
            FontSize = 12,
            Foreground = FindResource("InkSecondary") as Brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _modeSwitch.Content = "⇄ 改进";
        _modeSwitch.Cursor = System.Windows.Input.Cursors.Hand;
        _modeSwitch.Background = Brushes.Transparent;
        _modeSwitch.BorderThickness = new Thickness(0);
        _modeSwitch.Foreground = FindResource("Accent") as Brush;
        _modeSwitch.Click += (_, _) => SwitchMode();
        System.Windows.Controls.Grid.SetColumn(title, 0);
        System.Windows.Controls.Grid.SetColumn(_modeSwitch, 1);
        header.Children.Add(title);
        header.Children.Add(_modeSwitch);
        root.Children.Add(header);
        _titleText = title;

        _composer.AcceptsReturn = false;
        _composer.TextWrapping = TextWrapping.Wrap;
        _composer.FontSize = 14;
        _composer.Padding = new Thickness(0);
        _composer.Margin = new Thickness(0, 8, 0, 0);
        _composer.BorderThickness = new Thickness(0);
        _composer.Background = Brushes.Transparent;
        _composer.Foreground = FindResource("Ink") as Brush;
        _composer.KeyDown += OnComposerKeyDown;
        root.Children.Add(_composer);

        _result.TextWrapping = TextWrapping.Wrap;
        _result.FontSize = 15;
        _result.Margin = new Thickness(0, 12, 0, 0);
        _result.Foreground = FindResource("Ink") as Brush;
        root.Children.Add(_result);

        _note.FontSize = 12;
        _note.Margin = new Thickness(0, 8, 0, 0);
        _note.Foreground = FindResource("InkSecondary") as Brush;
        _note.Visibility = Visibility.Collapsed;
        root.Children.Add(_note);

        Content = root;

        SourceInitialized += (_, _) =>
        {
            PlaceNearCaret();
            MakeNonActivating();
        };
        Deactivated += (_, _) => { };
        PreviewKeyDown += OnPanelKeyDown;

        Loaded += async (_, _) => await BringInSelectionAsync();
        _composer.LostKeyboardFocus += (_, _) => { };
    }

    private readonly System.Windows.Controls.TextBlock _titleText;

    private void SwitchMode()
    {
        _mode = _mode == ProcessingMode.Translate ? ProcessingMode.Improve : ProcessingMode.Translate;
        _titleText.Text = _mode == ProcessingMode.Translate ? "翻译" : "改进";
        _modeSwitch.Content = _mode == ProcessingMode.Translate ? "⇄ 改进" : "⇄ 翻译";
    }

    private async Task BringInSelectionAsync()
    {
        var submission = await _model.ReadSelectionAsync();
        if (submission is { } value)
        {
            _composer.Text = value.Text;
            _composer.Select(value.Text.Length, 0);
            SubmitCurrent();
        }
        else
        {
            _composer.Focus();
        }
    }

    private void OnComposerKeyDown(object sender, System.Windows.Input.KeyEventArgs arguments)
    {
        if (arguments.Key == System.Windows.Input.Key.Return
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            arguments.Handled = true;
            SubmitCurrent();
        }
        if (arguments.Key == System.Windows.Input.Key.Tab)
        {
            arguments.Handled = true;
            SwitchMode();
        }
    }

    private void OnPanelKeyDown(object sender, System.Windows.Input.KeyEventArgs arguments)
    {
        if (arguments.Key == System.Windows.Input.Key.Escape)
        {
            arguments.Handled = true;
            if (_phase == ResultPhase.Streaming)
            {
                StopResult();
            }
            else
            {
                Hide();
            }
        }
        if (arguments.Key == System.Windows.Input.Key.Return
            && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, _result)
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            arguments.Handled = true;
            SubmitCurrent();
        }
    }

    public void SubmitCurrent()
    {
        var text = _composer.Text.Trim();
        if (text.Length == 0) return;
        _model.Submit(this, text, _mode);
    }

    // MARK: result phases

    public void BeginResult(ProcessingMode mode, string source)
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        _phase = ResultPhase.Streaming;
        _result.Text = "";
        _note.Visibility = Visibility.Collapsed;
        // A faint caret while the first characters travel.
        _result.Text = "▍";
    }

    public void AppendResult(string piece)
    {
        if (_result.Text == "▍")
        {
            _result.Text = piece;
        }
        else
        {
            _result.Text += piece;
        }
    }

    public void CompleteResult()
    {
        _phase = ResultPhase.Completed;
        if (_result.Text == "▍") _result.Text = "";
    }

    public void FailResult(string message)
    {
        _phase = ResultPhase.Failed;
        _note.Text = $"请求失败：{message} 按 ⏎ 重试";
        _note.Visibility = Visibility.Visible;
    }

    public void StopResult()
    {
        _cancellation?.Cancel();
        _phase = ResultPhase.Stopped;
        if (_result.Text.EndsWith("▍")) _result.Text = _result.Text[..^1];
        _note.Text = "已停止 · ⏎ 重新生成";
        _note.Visibility = Visibility.Visible;
    }

    public void BindCancellation(CancellationTokenSource cancellation)
    {
        _cancellation?.Cancel();
        _cancellation = cancellation;
    }

    /// <summary>Copies the result; the tray and the Escape flow stay the only other exits.</summary>
    protected override void OnMouseRightButtonUp(System.Windows.Input.MouseButtonEventArgs arguments)
    {
        base.OnMouseRightButtonUp(arguments);
        if (_result.Text.Length > 0)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(_result.Text);
                _note.Text = "已复制";
                _note.Visibility = Visibility.Visible;
            }
            catch
            {
                // The clipboard can be momentarily held by another process.
            }
        }
    }

    // MARK: placement and activation

    private void PlaceNearCaret()
    {
        // Near the caret or the mouse, clamped to the working area of its screen.
        var position = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(position);
        var working = screen.WorkingArea;
        Left = Math.Min(
            Math.Max(position.X - Width / 2, working.Left + 8),
            working.Right - Width - 8);
        Top = Math.Min(
            Math.Max(position.Y + 20, working.Top + 8),
            working.Bottom - 240);
    }

    private void MakeNonActivating()
    {
        // WS_EX_NOACTIVATE keeps the source application focused while the panel is visible.
        var helper = new WindowInteropHelper(this);
        var style = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
