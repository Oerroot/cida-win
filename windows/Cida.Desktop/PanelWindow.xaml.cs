using Cida.Core;
using Cida.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cida.Desktop;

public partial class PanelWindow : Window
{
    public sealed record Submission(string Text, SelectionReader Reader);
    private readonly AppModel _model;
    private readonly TextBox _composer;
    public PanelState State { get; } = new();
    private CancellationTokenSource? _cancellation;
    private readonly DispatcherTimer _renderClock = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private long _stateSerial;
    private int _selectionSerial;
    private string _lastImported = "";
    private string _rendered = "";
    private bool _composing;
    private bool _renderQueued;
    private bool _formatted;
    private bool _recognizing;
    private ProcessingAction? _retainedAction;
    private IReadOnlyList<ProcessingAction> DisplayedActions => _retainedAction != null && _model.Settings.EnabledActions.All(a => a.Id != _retainedAction.Id)
        ? _model.Settings.EnabledActions.Append(_retainedAction).ToArray() : _model.Settings.EnabledActions;
    private DateTime _shownAt;

    public PanelWindow(AppModel model)
    {
        _model = model; ThemeService.Apply(); InitializeComponent(); _composer = Composer;
        ShowActivated = false;
        if (Environment.GetEnvironmentVariable("CIDA_UI_INSPECTION") == "1") ShowInTaskbar = true;
        State.ActionId = model.Settings.EnabledActions.First().Id;
        RefreshActions(); RefreshConfiguration();
        PreviewKeyDown += OnPanelKeyDown;
        TextCompositionManager.AddPreviewTextInputStartHandler(Composer, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(Composer, (_, _) => _composing = false);
        _renderClock.Tick += (_, _) => { if (_renderQueued) RenderResult(); };
        _renderClock.Start();
        IsVisibleChanged += (_, _) => { if (IsVisible) _shownAt = DateTime.UtcNow; DevelopmentTrace.Stage("panel visible=" + IsVisible); };
        SourceInitialized += (_, _) => WindowPlacement.NearCursor(this);
        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        Closed += (_, _) => { _cancellation?.Cancel(); _renderClock.Stop(); };
        Deactivated += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("CIDA_UI_INSPECTION") != "1" && !_composing && IsVisible && !_model.IsSettingsVisible && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(300)) Hide();
        };
    }
    public void RefreshConfiguration()
    {
        ConfigureButton.Visibility = _model.Settings.IsModelServiceComplete ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = _model.Settings.IsModelServiceComplete
            ? "输入后按 Enter 执行 · Tab 切换动作"
            : "先配置模型服务，再开始翻译与改进。";
        RefreshActions();
    }
    public void RefreshActions()
    {
        if (!IsInitialized) return;
        var actions = DisplayedActions;
        if (actions.All(a => a.Id != State.ActionId)) State.ActionId = actions.First().Id;
        ActionButtons.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Name, Style = (Style)FindResource("ActionButton"), Tag = action.Id == State.ActionId ? "selected" : "idle", ToolTip = action.Name + " · Tab 切换" };
            button.Click += (_, _) => { State.ActionId = action.Id; RefreshActions(); RefreshStatus(); };
            ActionButtons.Children.Add(button);
        }
    }
    public async Task BringInSelectionAsync()
    {
        WindowPlacement.NearCursor(this);
        var serial = ++_selectionSerial;
        var submission = await _model.ReadSelectionAsync();
        if (serial != _selectionSerial || !IsVisible) return;
        if (submission is { } value && value.Text != _lastImported)
        {
            _lastImported = value.Text;
            SetSourceText(value.Text);
            State.ActionId = _model.Settings.EnabledActions.First().Id; RefreshActions();
            SubmitCurrent();
        }
        Activate(); Composer.Focus(); Composer.CaretIndex = Composer.Text.Length;
    }
    public void FocusInput() { Activate(); Composer.Focus(); }
    public void SetSourceText(string text) { ++_selectionSerial; Composer.Text = text; }
    public void UseTranslation() { State.ActionId = ProcessingAction.TranslationId; RefreshActions(); }
    private void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        State.Source = Composer.Text;
        if (SourceCount == null) return;
        SourceCount.Text = Composer.Text.Length == 0 ? "" : $"{Composer.Text.Length:N0} 字符";
        ComposerHint.Visibility = Composer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }
    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (_composing || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape) { Hide(); e.Handled = true; }
        else if (key == Key.Enter && modifiers == ModifierKeys.None) { SubmitCurrent(); e.Handled = true; }
        else if (key == Key.Tab && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
        {
            var actions = DisplayedActions;
            var index = actions.ToList().FindIndex(a => a.Id == State.ActionId);
            State.ActionId = actions[(index + (modifiers == ModifierKeys.Shift ? actions.Count - 1 : 1)) % actions.Count].Id;
            RefreshActions(); RefreshStatus(); e.Handled = true;
        }
        else if (key == Key.OemPeriod && modifiers == ModifierKeys.Control) { _model.StopRecognition(); StopResult(); e.Handled = true; }
        else if (key == Key.OemComma && modifiers == ModifierKeys.Control) { Hide(); _model.ShowSettings(); e.Handled = true; }
        else if (key == Key.L && modifiers == ModifierKeys.Control) { ShowLanguageEditor(); e.Handled = true; }
        else if (key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { CopyImage(); e.Handled = true; }
        else if (key == Key.C && modifiers == ModifierKeys.Control)
        {
            var hasSelection = Composer.IsKeyboardFocusWithin ? Composer.SelectionLength > 0 : ResultView.IsKeyboardFocusWithin && !ResultView.Selection.IsEmpty;
            if (!hasSelection) { CopyResult(); e.Handled = true; }
        }
    }
    private void ShowLanguageEditor()
    {
        var editor = new TextBox { Text = _model.Settings.ForeignLanguage, MinWidth = 200 };
        var popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = ActionButtons, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false };
        var save = new Button { Content = "应用", Margin = new Thickness(8, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(editor); row.Children.Add(save);
        var content = new StackPanel(); content.Children.Add(new TextBlock { Text = "常用外语", Margin = new Thickness(0, 0, 0, 8) }); content.Children.Add(row);
        var border = new Border { Child = content, Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); border.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        popup.Child = border;
        void Apply()
        {
            var language = editor.Text.Trim();
            if (language.Length == 0 || language.Contains('\n')) return;
            try { _model.SaveSettings(_model.Settings with { ForeignLanguage = language }); popup.IsOpen = false; RefreshStatus(); }
            catch (Exception) { Note.Text = "语言设置保存失败，请在设置中重试。"; }
        }
        save.Click += (_, _) => Apply(); editor.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        popup.IsOpen = true; editor.Focus(); editor.SelectAll();
    }
    public void SubmitCurrent()
    {
        if (!ExecuteButton.IsEnabled) return;
        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text)) { Composer.Focus(); return; }
        var action = DisplayedActions.First(a => a.Id == State.ActionId);
        _model.Submit(this, text, action);
    }
    public void RetainResult(ProcessingAction action, string source, string result, string note)
    {
        _retainedAction = action; SetSourceText(source); State.ActionId = action.Id; RefreshActions();
        BeginResult(action, source); AppendResult(result); CompleteResult(); ShowMessage(note);
    }
    public void BeginResult(ProcessingAction action, string source)
    {
        _cancellation?.Cancel();
        _stateSerial = State.Begin(source, action.Id, _model.RequestFingerprint(action));
        _rendered = ""; _formatted = false; ResultView.Document.Blocks.Clear(); RetryCapture.Visibility = Visibility.Collapsed;
        ResultTitle.Text = action.Id == ProcessingAction.TranslationId ? "译文" : action.Name + "结果";
        EmptyTitle.Text = "正在处理…"; EmptyHint.Text = "结果会在这里逐步显示。"; ConfigureButton.Visibility = Visibility.Collapsed;
        _renderQueued = true; RenderResult();
    }
    public void AppendResult(string piece) { if (State.Append(_stateSerial, piece)) _renderQueued = true; }
    public void CompleteResult() { State.Finish(_stateSerial, ResultPhase.Completed); _renderQueued = true; RenderResult(); }
    public void FailResult(string message) { State.Finish(_stateSerial, ResultPhase.Failed, message); _renderQueued = true; RenderResult(); }
    public void StopResult() { _cancellation?.Cancel(); State.Stop(); _renderQueued = true; RenderResult(); }
    public void BindCancellation(CancellationTokenSource cancellation) { _cancellation?.Cancel(); _cancellation = cancellation; }
    public void UnbindCancellation(CancellationTokenSource cancellation) { if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; }
    public void ShowMessage(string message, bool retryCapture = false)
    {
        Note.Text = message; RetryCapture.Visibility = retryCapture ? Visibility.Visible : Visibility.Collapsed;
    }
    public void ShowRecognitionState(string? error = null)
    {
        _recognizing = error == null;
        UseTranslation(); ShowMessage(error ?? "正在本地识别文字…", error != null);
        ExecuteButton.IsEnabled = error != null;
        StopButton.Visibility = error == null ? Visibility.Visible : Visibility.Collapsed;
    }
    public void FinishRecognition() { _recognizing = false; ExecuteButton.IsEnabled = true; StopButton.Visibility = State.Phase == ResultPhase.Streaming ? Visibility.Visible : Visibility.Collapsed; }
    private void RenderResult()
    {
        _renderQueued = false;
        var result = State.Result;
        var follow = ResultView.VerticalOffset >= ResultView.ExtentHeight - ResultView.ViewportHeight - 24;
        if (result != _rendered || (State.Phase != ResultPhase.Streaming && !_formatted))
        {
            if (State.Phase == ResultPhase.Streaming)
            {
                if (ResultView.Document.Blocks.Count == 0) ResultView.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
                ((Paragraph)ResultView.Document.Blocks.FirstBlock).Inlines.Add(new Run(result[_rendered.Length..]));
            }
            else { ResultDocument.Render(ResultView.Document, result); _formatted = true; }
            _rendered = result;
            if (follow) ResultView.ScrollToEnd();
        }
        EmptyResult.Visibility = result.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = ImageButton.IsEnabled = result.Length > 0;
        RefreshStatus();
    }
    private void RefreshStatus()
    {
        if (Note == null) return;
        StopButton.Visibility = _recognizing || State.Phase == ResultPhase.Streaming ? Visibility.Visible : Visibility.Collapsed;
        if (_recognizing) return;
        ExecuteButton.Content = State.HasSubmission ? "重新生成 ↵" : "执行 ↵";
        PhaseLabel.Text = !State.HasSubmission ? "" : State.Phase switch
        { ResultPhase.Streaming => "生成中", ResultPhase.Stopped => "已停止", ResultPhase.Failed => "失败", _ => "" };
        var action = _model.Settings.EffectiveActions.FirstOrDefault(a => a.Id == State.SubmittedActionId);
        var changedConfig = action != null && State.SubmittedConfiguration != _model.RequestFingerprint(action);
        Note.SetResourceReference(TextBlock.ForegroundProperty, State.Phase == ResultPhase.Failed ? "Error" : "InkSecondary");
        Note.Text = State.IsStale ? State.StaleReason + " · Enter 重新生成"
            : changedConfig ? "语言或配置已改变 · Enter 重新生成"
            : State.Phase == ResultPhase.Failed ? "请求失败：" + State.Error + " · 可重新生成"
            : State.Phase == ResultPhase.Stopped ? "已停止，已有结果已保留。"
            : State.HasSubmission ? "" : SettingsFileStore.RecoveryNote ?? "Enter 执行 · Shift+Enter 换行";
    }
    private void CopyResult()
    {
        if (State.Result.Length == 0) return;
        try { System.Windows.Clipboard.SetText(State.Result); Note.Text = "已复制"; }
        catch (System.Runtime.InteropServices.ExternalException) { Note.Text = "剪贴板正忙，请重试。"; }
    }
    private void CopyImage()
    {
        if (State.Result.Length == 0) return;
        try { System.Windows.Clipboard.SetImage(ResultDocument.Image(State.Result, ResultTitle.Text)); Note.Text = "已复制图片"; }
        catch (InvalidOperationException error) { Note.Text = error.Message; }
        catch (Exception) { Note.Text = "图片复制失败，请重试或复制文字。"; }
    }
    private void OnExecute(object sender, RoutedEventArgs e) => SubmitCurrent();
    private void OnStop(object sender, RoutedEventArgs e) { _model.StopRecognition(); StopResult(); }
    private void OnCopy(object sender, RoutedEventArgs e) => CopyResult();
    private void OnCopyImage(object sender, RoutedEventArgs e) => CopyImage();
    private void OnSettings(object sender, RoutedEventArgs e) { Hide(); _model.ShowSettings(); }
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnRetryCapture(object sender, RoutedEventArgs e) => _model.StartCapture();
}
