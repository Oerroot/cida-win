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
    private string _imageTitle = "译文";
    private string? _failureMessage;
    private string? _submittedActionPrompt;
    private IReadOnlyList<ProcessingAction> DisplayedActions => _retainedAction != null && _model.Settings.EnabledActions.All(a => a.Id != _retainedAction.Id)
        ? _model.Settings.EnabledActions.Append(_retainedAction).ToArray() : _model.Settings.EnabledActions;
    private DateTime _shownAt;

    public PanelWindow(AppModel model)
    {
        _model = model; ThemeService.Apply(); InitializeComponent(); _composer = Composer;
        Style = (Style)FindResource(typeof(Window));
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
        SourceInitialized += (_, _) => { ThemeService.ApplyWindowFrame(this); WindowPlacement.NearCursor(this); RefreshBrandIcon(); };
        DpiChanged += (_, _) => RefreshBrandIcon();
        Closed += (_, _) => { _cancellation?.Cancel(); _renderClock.Stop(); };
        Deactivated += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("CIDA_UI_INSPECTION") != "1" && !_composing && IsVisible && !_model.IsSettingsVisible && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(300)) Hide();
        };
    }
    private void OnCaptionSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The native caption starts at the window's top edge, including the outer
        // border and all header padding. Only the two buttons opt back into client input.
        var captionBottom = CaptionBar.TranslatePoint(new System.Windows.Point(0, CaptionBar.ActualHeight), this).Y;
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = captionBottom;
    }
    private void RefreshBrandIcon() => BrandIcon.Source = BrandAssets.LoadPanelImage(VisualTreeHelper.GetDpi(this).DpiScaleX);
    public void RefreshConfiguration()
    {
        ConfigureButton.Visibility = _model.Settings.IsModelServiceComplete ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = _model.Settings.IsModelServiceComplete
            ? "输入后按 Enter 执行 · Tab 切换动作"
            : "先配置模型服务，再开始翻译与改进。";
        RefreshActions();
        // A reloaded configuration changes whether a configuration failure still owns the
        // recovery state (the bar action switches from 模型设置 to 重试), so refresh the status too.
        RefreshStatus();
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
    /// <summary>
    /// Runs the control bar's action (#73): a configuration failure whose fingerprint still
    /// matches the current settings opens model settings instead, and Return obeys the same
    /// gate, so a failed request is never re-sent against a configuration that is still wrong.
    /// </summary>
    public void SubmitCurrent()
    {
        var input = Presentation();
        if (PanelPresentation.ResolveBarAction(input) == PanelBarAction.ModelSettings)
        {
            OpenModelSettings();
            return;
        }
        if (!PanelPresentation.CanSubmit(input)) { Composer.Focus(); return; }
        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text)) { Composer.Focus(); return; }
        var action = DisplayedActions.First(a => a.Id == State.ActionId);
        _model.Submit(this, text, action);
    }
    private void OpenModelSettings() { Hide(); _model.ShowModelSettings(); }
    public void RetainResult(ProcessingAction action, string source, string result, string note)
    {
        _retainedAction = action; SetSourceText(source); State.ActionId = action.Id; RefreshActions();
        BeginResult(action, source); AppendResult(result); CompleteResult(); ShowMessage(note);
    }
    public void BeginResult(ProcessingAction action, string source)
    {
        _cancellation?.Cancel();
        _stateSerial = State.Begin(source, action.Id, _model.RequestFingerprint(action));
        _submittedActionPrompt = action.Prompt;
        _failureMessage = null;
        _imageTitle = action.Id == ProcessingAction.TranslationId ? "译文" : action.Name + "结果";
        _rendered = ""; _formatted = false; ResultView.Document.Blocks.Clear(); RetryCapture.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "正在处理…"; EmptyHint.Text = "结果会在这里逐步显示。"; ConfigureButton.Visibility = Visibility.Collapsed;
        _renderQueued = true; RenderResult();
    }
    public void AppendResult(string piece) { if (State.Append(_stateSerial, piece)) _renderQueued = true; }
    public void CompleteResult()
    {
        if (State.Finish(_stateSerial, ResultPhase.Completed)) _failureMessage = null;
        _renderQueued = true; RenderResult();
    }

    /// <summary>
    /// Ends the running request with safe recovery information (#73). The streamed text stays
    /// as the result, so the copy label can say 复制已有文字; <paramref name="message"/> is an
    /// optional friendly note and never provider text.
    /// </summary>
    public void FailResult(ProcessingFailure failure, string? message = null)
    {
        _failureMessage = message;
        State.Fail(_stateSerial, failure);
        _renderQueued = true; RenderResult();
    }

    /// <summary>
    /// Compatibility entry point for the local visual fixtures: an ordinary failure with no
    /// recovery category still gets the unknown-category recovery path instead of raw prose.
    /// </summary>
    public void FailResult(string message) => FailResult(
        new ProcessingFailure(ProcessingFailureCategory.Unknown,
            _model.Settings.ModelService.Model, _model.Settings.ModelServiceFingerprint), message);
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
        UseTranslation();
        RefreshActionControls(Presentation());
        ShowMessage(error ?? "正在本地识别文字…", error != null);
    }
    public void FinishRecognition() { _recognizing = false; RefreshActionControls(Presentation()); }
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
        if (State.HasSubmission)
        {
            EmptyTitle.Text = State.Phase switch
            {
                ResultPhase.Streaming => "正在处理…",
                ResultPhase.Failed => "请求未完成",
                ResultPhase.Stopped => "已停止",
                _ => EmptyTitle.Text,
            };
            EmptyHint.Text = State.Phase switch
            {
                ResultPhase.Streaming => "结果会在这里逐步显示。",
                ResultPhase.Failed => "已有文字仍可复制；按失败说明处理后重试。",
                ResultPhase.Stopped => "已有结果已保留。",
                _ => EmptyHint.Text,
            };
        }
        CopyButton.IsEnabled = ImageButton.IsEnabled = result.Length > 0;
        RefreshStatus();
    }
    private PanelPresentationInput Presentation()
    {
        return new PanelPresentationInput
        {
            IsProcessing = State.Phase == ResultPhase.Streaming,
            IsRecognizing = _recognizing,
            IsStale = State.IsStale || (_submittedActionPrompt != null
                && DisplayedActions.FirstOrDefault(action => action.Id == State.ActionId)?.Prompt != _submittedActionPrompt),
            HasSubmittableInput = !string.IsNullOrWhiteSpace(Composer.Text),
            Phase = State.Phase,
            Failure = State.Failure,
            CurrentConfigurationFingerprint = _model.Settings.ModelServiceFingerprint,
            RepeatsSubmittedAction = State.SubmittedActionId == State.ActionId,
            HasOutput = State.Result.Length > 0,
        };
    }
    private void RefreshStatus()
    {
        if (Note == null) return;
        var input = Presentation();
        RefreshActionControls(input);
        ResultTitle.Text = PanelPresentation.ResultTitle(input);
        PhaseLabel.Text = PanelPresentation.ResultDetail(input);
        var explanation = PanelPresentation.FailureExplanation(input);
        FailureExplanation.Text = explanation ?? "";
        FailureExplanation.Visibility = explanation == null ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Content = PanelPresentation.CopyLabel(input);
        var submitted = _model.Settings.EffectiveActions.FirstOrDefault(a => a.Id == State.SubmittedActionId);
        var changedConfig = submitted != null && State.SubmittedConfiguration != _model.RequestFingerprint(submitted);
        Note.SetResourceReference(TextBlock.ForegroundProperty, State.Phase == ResultPhase.Failed ? "Error" : "InkSecondary");
        Note.Text = State.Failure != null ? _failureMessage ?? ""
            : input.IsStale ? State.StaleReason + " · Enter 重新生成"
            : changedConfig ? "语言或配置已改变 · Enter 重新生成"
            : State.Phase == ResultPhase.Failed ? "请求失败：" + State.Error + " · 可重新生成"
            : State.Phase == ResultPhase.Stopped ? "已停止，已有结果已保留。"
            : State.HasSubmission ? "" : SettingsFileStore.RecoveryNote ?? "Enter 执行 · Shift+Enter 换行";
    }
    /// <summary>The control bar only; recognition updates it without overwriting its own note.</summary>
    private void RefreshActionControls(PanelPresentationInput input)
    {
        var action = PanelPresentation.ResolveBarAction(input);
        StopButton.Visibility = _recognizing || State.Phase == ResultPhase.Streaming ? Visibility.Visible : Visibility.Collapsed;
        ExecuteButton.Visibility = action == PanelBarAction.Stop ? Visibility.Collapsed : Visibility.Visible;
        ExecuteButton.Content = action == PanelBarAction.ModelSettings ? "模型设置" : PanelPresentation.ActionLabel(action) + " ↵";
        ExecuteButton.ToolTip = PanelPresentation.ActionToolTip(action);
        // The bar button is also the way back to the failing configuration, so it stays
        // clickable in the model-settings state even though Return is gated.
        ExecuteButton.IsEnabled = action == PanelBarAction.ModelSettings || PanelPresentation.CanSubmit(input);
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
        // An unfinished result leaves the panel as a marked image (#73): the text stays
        // verbatim for text copy, while the image shows the interruption.
        try { System.Windows.Clipboard.SetImage(ResultDocument.Image(State.Result, _imageTitle, State.Phase)); Note.Text = "已复制图片"; }
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

/// <summary>What the panel's control bar offers for the current state (#73).</summary>
public enum PanelBarAction
{
    Stop,
    Execute,
    Reexecute,
    Retry,
    ModelSettings,
}

/// <summary>
/// The pure inputs of the panel's recovery presentation. Kept free of WPF and window state so
/// the decisions can be tested headlessly, matching upstream AppModel's computed properties
/// (resultCopyLabel / barActionPresentation / canSubmit / failureExplanation).
/// </summary>
public sealed record PanelPresentationInput
{
    /// <summary>A request is streaming.</summary>
    public bool IsProcessing { get; init; }

    /// <summary>Local OCR is reading the capture; execution is disabled meanwhile.</summary>
    public bool IsRecognizing { get; init; }

    /// <summary>The source or the action changed since the result was requested.</summary>
    public bool IsStale { get; init; }

    /// <summary>The composer holds text that can be submitted.</summary>
    public bool HasSubmittableInput { get; init; }

    public ResultPhase Phase { get; init; } = ResultPhase.Completed;

    /// <summary>The safe recovery information of the last request, if it failed.</summary>
    public ProcessingFailure? Failure { get; init; }

    /// <summary>The current model service fingerprint, compared with the failed configuration.</summary>
    public string CurrentConfigurationFingerprint { get; init; } = "";

    /// <summary>The selected action is the one that produced the result.</summary>
    public bool RepeatsSubmittedAction { get; init; }

    /// <summary>Some output streamed before the failure, so the result is incomplete, not empty.</summary>
    public bool HasOutput { get; init; }
}

/// <summary>
/// The panel's failure recovery and incomplete-result presentation (#73). These are pure
/// functions of <see cref="PanelPresentationInput"/> so the behaviour is testable without a
/// window; <see cref="PanelWindow"/> only maps the results onto its controls.
/// </summary>
public static class PanelPresentation
{
    /// <summary>The failed request's configuration no longer matches the current settings.</summary>
    public static bool ConfigurationChanged(PanelPresentationInput input) =>
        input.Failure is { } failure && failure.ConfigurationFingerprint != input.CurrentConfigurationFingerprint;

    /// <summary>
    /// Upstream barActionPresentation: processing stops; an unchanged configuration failure
    /// sends the user to model settings; a changed configuration or a transient failure offers
    /// an explicit retry; a stale failure submits a new request; otherwise execute/reexecute.
    /// </summary>
    public static PanelBarAction ResolveBarAction(PanelPresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsProcessing) return PanelBarAction.Stop;
        if (input.Failure is { } failure && !input.IsStale)
        {
            if (failure.Category == ProcessingFailureCategory.Configuration && !ConfigurationChanged(input))
                return PanelBarAction.ModelSettings;
            return PanelBarAction.Retry;
        }
        // A stale failure only explains the previous request: submitting starts a new one.
        if (input.Failure != null) return PanelBarAction.Execute;
        return input.RepeatsSubmittedAction ? PanelBarAction.Reexecute : PanelBarAction.Execute;
    }

    public static string ActionLabel(PanelBarAction action) => action switch
    {
        PanelBarAction.Stop => "停止",
        PanelBarAction.Execute => "执行",
        PanelBarAction.Reexecute => "重新执行",
        PanelBarAction.Retry => "重试",
        PanelBarAction.ModelSettings => "模型设置",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string ActionToolTip(PanelBarAction action) => action switch
    {
        PanelBarAction.Stop => "停止 · Ctrl+.",
        PanelBarAction.ModelSettings => "模型设置 · Ctrl+,",
        _ => ActionLabel(action) + " · Enter",
    };

    /// <summary>Upstream canSubmit: text is present, nothing runs, and Return is not gated.</summary>
    public static bool CanSubmit(PanelPresentationInput input) =>
        input.HasSubmittableInput && !input.IsProcessing && !input.IsRecognizing
        && ResolveBarAction(input) != PanelBarAction.ModelSettings;

    /// <summary>Upstream resultCopyLabel: a failed request copies the text it already has.</summary>
    public static string CopyLabel(PanelPresentationInput input) =>
        input.Failure != null ? "复制已有文字"
        : input.IsStale ? "复制上次结果"
        : "复制结果";

    /// <summary>Upstream ResultHeader.title: what the result area currently shows.</summary>
    public static string ResultTitle(PanelPresentationInput input)
    {
        if (input.IsProcessing) return "正在执行";
        if (input.Failure != null)
        {
            // Partial output makes the failure an incomplete result, not an empty one.
            var state = input.HasOutput ? "未完成" : "失败";
            return input.IsStale ? "上次请求" + state : state == "失败" ? "请求失败" : state;
        }
        if (input.IsStale) return "上次结果";
        return input.Phase == ResultPhase.Stopped ? "已停止" : "结果";
    }

    /// <summary>Upstream ResultHeader's secondary text: the failed model, else the stale mark.</summary>
    public static string ResultDetail(PanelPresentationInput input)
    {
        if (input.Failure is { ModelName.Length: > 0 } failure) return "· " + failure.ModelName;
        return input.IsStale ? "· 待更新" : "";
    }

    /// <summary>Upstream failureExplanation: stale and changed-configuration failures explain themselves.</summary>
    public static string? FailureExplanation(PanelPresentationInput input)
    {
        if (input.Failure is not { } failure) return null;
        if (input.IsStale) return "原文或动作已经改了。执行会发送新的请求；这条失败说明只针对上次请求。";
        if (ConfigurationChanged(input)) return "模型配置已更新。按 Enter 或点「重试」重新发送请求，并替换已有输出。";
        return failure.Explanation;
    }

    /// <summary>The image mark of an unfinished result; empty for a finished one (upstream ShareCard.IncompleteMark).</summary>
    public static string? IncompleteMarkText(ResultPhase phase) => phase switch
    {
        ResultPhase.Failed => "未完成 · 请求中断",
        ResultPhase.Stopped => "未完成 · 生成时已停止",
        _ => null,
    };

    /// <summary>
    /// The result text drawn on a copied image: unfinished output gets an ellipsis so the image
    /// never looks like a complete answer. Text copy itself stays verbatim.
    /// </summary>
    public static string ImageResultText(string result, ResultPhase phase) =>
        IncompleteMarkText(phase) == null ? result : result + "…";
}
