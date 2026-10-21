using Cida.Core;
using Cida.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace Cida.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly CidaSettings _baseline;
    private readonly TextBlock _status;
    private List<ProcessingAction> _actions;
    private readonly Dictionary<GlobalShortcutAction, GlobalShortcut?> _shortcuts = new();
    private readonly Dictionary<GlobalShortcutAction, TextBox> _shortcutEditors = new();
    private readonly Dictionary<GlobalShortcutAction, Button> _shortcutResetButtons = new();
    private bool _loadingAction;
    private int _editing = -1;
    private CancellationTokenSource? _checkCancellation;
    private bool _closed;
    private string? _actionNotice;
    private volatile bool _actionRefreshPending;
    private UpdatePhase? _dismissedUpdatePhase;
    public sealed record ProtocolOption(string Title, string Value);

    public SettingsWindow(AppModel model)
    {
        _model = model; _baseline = model.Settings; _actions = _baseline.EffectiveActions.ToList();
        ThemeService.Apply(); InitializeComponent(); _status = CheckStatus;
        Style = (Style)FindResource(typeof(Window));
        SourceInitialized += (_, _) => { ThemeService.ApplyWindowFrame(this); WindowPlacement.Settings(this); };
        if (Environment.GetEnvironmentVariable("CIDA_UI_INSPECTION") == "1") ShowInTaskbar = true;
        Format.ItemsSource = ModelRequestFormatExtensions.AllCases.Select(f => new ProtocolOption(f.DisplayName(), f.RawValue())).ToList();
        Format.SelectedValue = _baseline.ModelService.Format.RawValue();
        Endpoint.Text = _baseline.ModelService.Endpoint; ModelName.Text = _baseline.ModelService.Model;
        Authentication.ItemsSource = new[] { "随协议", "bearer", "x-api-key", "api-key", "none" };
        Authentication.SelectedItem = _baseline.ModelService.Auth?.RawValue() ?? "随协议";
        KeyMode.SelectedIndex = 0;
        Headers.Text = ConfigurationField.Headers.FieldJson(new EditableConfiguration { Settings = _baseline }, false).DisplayText;
        Body.Text = JsonValue.Object(_baseline.ModelService.Body).DisplayText;
        var languages = _baseline.RequestLanguages(); MyLanguage.Text = languages.My; ForeignLanguage.Text = languages.Foreign;
        LaunchAtLogin.IsChecked = model.Store.LaunchAtLogin();
        ModelSummary.Text = _baseline.IsModelServiceComplete
            ? _baseline.ModelService.Model + " · " + _baseline.ModelService.Format.DisplayName() + "\n" + _baseline.ModelService.Endpoint
            : "尚未配置。支持 OpenAI、Anthropic 及兼容服务。";
        VersionLabel.Text = "辞达 Cida · " + UpdateService.Version + " · 候选版";
        BuildShortcutRows();
        ActionsList.ItemsSource = _actions;
        var initialAction = _actions.FindIndex(action => action.Id == _model.ActionEditor.SelectedId);
        ActionsList.SelectedIndex = initialAction >= 0 ? initialAction : 0;
        if (_actions.Count > 0) { _editing = ActionsList.SelectedIndex; LoadSelectedAction(); }
        RefreshShortcutCapabilities();
        RefreshUpdates();
        var initialPage = _model.TakeSettingsInitialPage();
        if (initialPage >= 0 && initialPage < Pages.Items.Count) Pages.SelectedIndex = initialPage;
        _model.Updates.Changed += OnUpdatesChanged;
        _model.ActionEditor.Changed += OnActionEditorChanged;
        _model.Updates.SetWatched(true);
        RefreshActionPage();
        Closed += (_, _) =>
        {
            _closed = true;
            _checkCancellation?.Cancel();
            _model.Updates.Changed -= OnUpdatesChanged;
            _model.ActionEditor.Changed -= OnActionEditorChanged;
            _model.Updates.SetWatched(false);
        };
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += async (_, _) => await RefreshCapabilitiesAsync();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        // One fixed-sample trial from the action page: the button stays the only confirmation.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && Pages.SelectedIndex == 1
            && PreviewButton.Visibility == Visibility.Visible && PreviewButton.IsEnabled)
        {
            OnPreview(PreviewButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private CidaSettings Draft()
    {
        var language = MyLanguage.Text.Trim(); var foreign = ForeignLanguage.Text.Trim();
        if (language.Contains('\n') || foreign.Contains('\n')) throw new InvalidOperationException("语言名称请写成一行。");
        if (_actions.Any(a => string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Prompt)))
            throw new InvalidOperationException("每个动作都需要名称和提示词。");
        var configuration = new EditableConfiguration { Settings = _baseline };
        configuration = ConfigurationField.Headers.Apply(Headers.Text, configuration);
        configuration = ConfigurationField.Body.Apply(Body.Text, configuration);
        var key = KeyMode.SelectedIndex switch { 1 => ApiKey.Password, 2 => "", _ => _baseline.ApiKey };
        if (KeyMode.SelectedIndex == 1 && key.Length == 0) throw new InvalidOperationException("请输入新密钥，或选择保留/清除。");
        var translation = _actions.First(a => a.Id == ProcessingAction.TranslationId);
        var improvement = _actions.FirstOrDefault(a => a.Id == ProcessingAction.ImprovementId);
        var settings = configuration.Settings with
        {
            ModelService = configuration.Settings.ModelService with
            {
                Endpoint = Endpoint.Text.Trim(), Model = ModelName.Text.Trim(),
                Format = ModelRequestFormatExtensions.FromRawValue(Format.SelectedValue as string) ?? ModelRequestFormat.ChatCompletions,
                Auth = Authentication.SelectedItem as string == "随协议" ? null : ModelAuthenticationExtensions.FromRawValue(Authentication.SelectedItem as string),
            },
            ApiKey = key, MyLanguage = language, ForeignLanguage = foreign,
            Actions = _actions.ToList(), TranslationPrompt = translation.Prompt,
            ImprovementPrompt = improvement?.Prompt ?? _baseline.ImprovementPrompt,
            LaunchAtLogin = LaunchAtLogin.IsChecked == true,
            Shortcut = _shortcuts[GlobalShortcutAction.ShowPanel], CaptureShortcut = _shortcuts[GlobalShortcutAction.CaptureText],
            LayerShortcut = _shortcuts[GlobalShortcutAction.TranslationLayer], ImproveShortcut = _shortcuts[GlobalShortcutAction.ImproveAndReplace],
        };
        if (settings.ModelService.Endpoint.Length > 0 && ModelConfiguration.EndpointUrlFrom(settings.ModelService.Endpoint) == null)
            throw new InvalidOperationException("端点必须是完整的 HTTP 或 HTTPS 地址。");
        if (!settings.HasValidShortcuts) throw new InvalidOperationException("快捷键不能重复；段落快捷键不能包含 Shift。");
        return settings;
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_model.Settings.ToJsonText() != _baseline.ToJsonText() || _model.Settings.ApiKey != _baseline.ApiKey)
                throw new InvalidOperationException("配置已被其他入口修改。请重新打开设置后保存。");
            var draft = Draft();
            var oldLogin = _model.Store.LaunchAtLogin();
            var touchedKey = KeyMode.SelectedIndex != 0;
            try
            {
                if (touchedKey) { if (draft.ApiKey.Length == 0) _model.Store.ClearApiKey(); else _model.Store.SaveApiKey(draft.ApiKey); }
                _model.Store.SetLaunchAtLogin(draft.LaunchAtLogin);
                _model.SaveSettings(draft);
            }
            catch
            {
                if (touchedKey) { if (_baseline.ApiKey.Length == 0) _model.Store.ClearApiKey(); else _model.Store.SaveApiKey(_baseline.ApiKey); }
                _model.Store.SetLaunchAtLogin(oldLogin); throw;
            }
            Close();
        }
        catch (Exception error) { SaveStatus.Text = error is InvalidOperationException or InvalidConfiguration ? error.Message : "保存失败，配置未生效，请重试。"; }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnKeyMode(object sender, SelectionChangedEventArgs e)
    { if (ApiKey != null) { ApiKey.IsEnabled = KeyMode.SelectedIndex == 1; if (!ApiKey.IsEnabled) ApiKey.Clear(); } }
    private async Task RunCheckAsync()
    {
        if (_checkCancellation != null) return;
        try
        {
            var draft = Draft();
            _checkCancellation = new(); CheckButton.IsEnabled = false; CancelCheck.Visibility = Visibility.Visible;
            CheckStatus.Text = "正在检查草稿配置…";
            var result = await ModelServiceCheck.RunAsync(draft, cancellationToken: _checkCancellation.Token);
            if (_closed) return;
            CheckStatus.Text = result.Passed ? "连接成功 · " + result.Model : "检查失败 · " + (result.Failure?.Reason() ?? "已取消或未配置完整");
        }
        catch (OperationCanceledException) { CheckStatus.Text = "检查已取消"; }
        catch (Exception error) { CheckStatus.Text = "检查失败 · " + (error is InvalidOperationException or InvalidConfiguration ? error.Message : "请检查配置后重试"); }
        finally { _checkCancellation?.Dispose(); _checkCancellation = null; CheckButton.IsEnabled = true; CancelCheck.Visibility = Visibility.Collapsed; }
    }
    private async void OnCheck(object sender, RoutedEventArgs e) => await RunCheckAsync();
    private void OnCancelCheck(object sender, RoutedEventArgs e) => _checkCancellation?.Cancel();
    private void OnCopyConfiguration(object sender, RoutedEventArgs e)
    {
        try
        {
            // The handoff follows the current task (#74): a panel request failure and the selected
            // action's trial failure are distinct contexts, and failures of an older configuration
            // are filtered out by ConfigurationPromptBuilder itself.
            var editor = _model.ActionEditor;
            var previewFailure = editor.Attempts.TryGetValue(editor.SelectedId, out var attempt) ? attempt.Failure : null;
            var prompt = ConfigurationPromptBuilder.Build(_model.Settings, null, _model.LastPanelFailure, previewFailure);
            System.Windows.Clipboard.SetText(prompt);
            SaveStatus.Text = "配置说明已复制，可交给 AI 助手。";
        }
        catch { SaveStatus.Text = "剪贴板正忙，请重试。"; }
    }

    // MARK: actions (#74)

    private void LoadSelectedAction()
    {
        if (_editing < 0 || _editing >= _actions.Count) return;
        var action = _actions[_editing];
        _loadingAction = true;
        ActionName.Text = action.Name; ActionPrompt.Text = action.Prompt; ActionEnabled.IsChecked = action.Enabled;
        ActionEnabled.IsEnabled = action.Id != ProcessingAction.TranslationId;
        DeleteAction.IsEnabled = action.Id != ProcessingAction.TranslationId;
        ResetPrompt.Visibility = action.Mode == ProcessingMode.Custom ? Visibility.Collapsed : Visibility.Visible;
        _loadingAction = false;
    }
    private void OnActionSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ActionName == null || ActionsList.SelectedIndex < 0) return;
        if (_model.ActionEditor.IsRunning) return;
        _editing = ActionsList.SelectedIndex;
        if (_editing >= _actions.Count) return;
        _actionNotice = null;
        LoadSelectedAction();
        _model.ActionEditor.Select(_actions[_editing].Id);
        RefreshActionPage();
    }
    private void OnActionEdited(object sender, TextChangedEventArgs e) => EditAction();
    private void OnActionToggled(object sender, RoutedEventArgs e) => EditAction();
    private void EditAction()
    {
        if (_loadingAction || _editing < 0 || _editing >= _actions.Count || ActionPrompt == null) return;
        _actionNotice = null;
        _actions[_editing] = _actions[_editing] with { Name = ActionName.Text, Prompt = ActionPrompt.Text, Enabled = ActionEnabled.IsChecked == true };
        ActionsList.Items.Refresh();
        RefreshActionPage();
    }
    private void SelectActions(int index)
    {
        ActionsList.ItemsSource = null; ActionsList.ItemsSource = _actions;
        ActionsList.SelectedIndex = Math.Clamp(index, 0, _actions.Count - 1);
    }
    private void OnAddAction(object sender, RoutedEventArgs e)
    {
        if (_model.ActionEditor.IsRunning) return;
        _actions.Add(new ProcessingAction { Name = "新动作", Prompt = "Rewrite the user-provided text concisely. Return only the rewritten text." });
        SelectActions(_actions.Count - 1);
    }
    private void OnDeleteAction(object sender, RoutedEventArgs e)
    {
        if (_editing < 0 || _editing >= _actions.Count || _actions[_editing].Id == ProcessingAction.TranslationId) return;
        if (_model.ActionEditor.IsRunning) return;
        var index = _editing; _actions.RemoveAt(index); _editing = -1; SelectActions(index);
    }
    private void MoveAction(int direction)
    {
        if (_model.ActionEditor.IsRunning) return;
        var index = _editing; var target = index + direction;
        if (index < 0 || target < 0 || target >= _actions.Count) return;
        (_actions[index], _actions[target]) = (_actions[target], _actions[index]); SelectActions(target);
    }
    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveAction(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveAction(1);
    private void OnResetPrompt(object sender, RoutedEventArgs e)
    { if (_editing >= 0 && _actions[_editing].Mode != ProcessingMode.Custom) ActionPrompt.Text = CidaSettings.DefaultPromptFor(_actions[_editing].Mode); }

    /// <summary>Restores the selected action's saved name, prompt and visibility, or drops an unsaved new action.</summary>
    private void OnDiscardAction(object sender, RoutedEventArgs e)
    {
        if (_editing < 0 || _editing >= _actions.Count || _model.ActionEditor.IsRunning) return;
        var index = _editing;
        var draft = _actions[index];
        var saved = _baseline.EffectiveActions.FirstOrDefault(action => action.Id == draft.Id);
        if (saved != null) _actions[index] = saved;
        else if (_actions.Count > 1) _actions.RemoveAt(index);
        _editing = -1;
        SelectActions(Math.Clamp(index, 0, _actions.Count - 1));
        _actionNotice = saved != null ? "已放弃修改，恢复为已保存的内容。" : "已放弃新建动作。";
        RefreshActionPage();
    }
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_editing < 0 || _editing >= _actions.Count || _model.ActionEditor.IsRunning) return;
        var editor = _model.ActionEditor;
        var action = _actions[_editing];
        var saved = _baseline.EffectiveActions.FirstOrDefault(candidate => candidate.Id == action.Id);
        _actionNotice = null;
        if (saved == null)
        {
            _actionNotice = "新动作需要保存设置后才能试运行。"; RefreshActionPage(); return;
        }
        if (!Equals(saved, action))
        {
            _actionNotice = "提示词有未保存的修改：试运行使用已保存的版本，先放弃修改或保存设置。"; RefreshActionPage(); return;
        }
        editor.Select(action.Id);
        editor.TrySavedAction(_baseline);
        RefreshActionPage();
    }
    private void OnCancelPreview(object sender, RoutedEventArgs e) => _model.ActionEditor.StopPreview();
    private void OnModelSettings(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    private void OnActionEditorChanged()
    {
        if (_closed || _actionRefreshPending) return;
        _actionRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _actionRefreshPending = false;
            if (!_closed) RefreshActionPage();
        }));
    }

    /// <summary>
    /// Paints the saved-action trial state: the fixed-sample result being streamed, the previous
    /// complete reference kept apart from it, the run/retry/stop commands, and the configuration
    /// entry after a configuration failure.
    /// </summary>
    private void RefreshActionPage()
    {
        if (_closed || PreviewButton == null || _editing < 0 || _editing >= _actions.Count) return;
        var action = _actions[_editing];
        var editor = _model.ActionEditor;
        var attempt = editor.Attempts.TryGetValue(action.Id, out var foundAttempt) ? foundAttempt : null;
        var preview = editor.Previews.TryGetValue(action.Id, out var foundPreview) ? foundPreview : null;
        var saved = _baseline.EffectiveActions.FirstOrDefault(candidate => candidate.Id == action.Id);
        var dirty = saved == null || !Equals(saved, action);
        var anyRunning = editor.IsRunning;
        var running = anyRunning && editor.SelectedId == action.Id;
        var needsConfiguration = saved != null && editor.NeedsConfiguration(_baseline);
        var stale = preview != null && saved != null && !preview.Matches(saved, _baseline);
        var failure = attempt?.Failure;

        // A running trial locks the list and the editor; browsing or changing the prompt mid-run
        // would separate the visible result from the action it belongs to.
        ActionsList.IsEnabled = !anyRunning;
        ActionListButtons.IsEnabled = !anyRunning;
        ActionName.IsEnabled = !anyRunning;
        ActionPrompt.IsEnabled = !anyRunning;
        ActionEnabled.IsEnabled = !anyRunning && action.Id != ProcessingAction.TranslationId;
        DiscardAction.IsEnabled = !anyRunning;
        ResetPrompt.IsEnabled = !anyRunning;

        ActionPromptTitle.Text = dirty ? "提示词 · 草稿未保存" : "提示词 · 已保存";
        DiscardAction.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        ResetPrompt.Visibility = action.Mode == ProcessingMode.Custom ? Visibility.Collapsed : Visibility.Visible;
        ActionEditHint.Text = !dirty ? "" : saved == null
            ? "新动作尚未保存；保存设置后才能用固定样本试运行。"
            : "提示词有未保存的修改：试运行使用已保存的版本，先放弃修改或保存设置。";
        ActionEditHint.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        var canTrial = !dirty && saved != null && !anyRunning && !needsConfiguration;
        PreviewButton.Visibility = needsConfiguration ? Visibility.Collapsed : Visibility.Visible;
        ModelSettingsButton.Visibility = needsConfiguration ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.IsEnabled = canTrial;
        PreviewButton.Content = failure != null ? "重试" : (preview != null || attempt != null) ? "再次运行" : "试运行";
        CancelPreview.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelPreview.IsEnabled = running;
        PreviewKeyHint.Visibility = canTrial ? Visibility.Visible : Visibility.Collapsed;

        string status;
        if (dirty) status = preview == null && attempt == null
            ? "草稿未保存：保存设置后生效，先放弃修改才能试运行。"
            : "正在编辑草稿；下方是已保存版本的结果。";
        else if (attempt != null)
        {
            status = attempt.Stage switch
            {
                ActionAttemptStage.Running => "运行中 · 已保存的提示词",
                ActionAttemptStage.Stopped => "已停止 · 未完成",
                ActionAttemptStage.Failed => failure == null ? "试运行失败"
                    : (attempt.Saved ? "已保存 · 试运行失败" : "试运行失败")
                        + (string.IsNullOrEmpty(failure.ModelName) ? "" : " · " + failure.ModelName),
                _ => "已保存的提示词",
            };
        }
        else if (preview == null) status = "还没有运行过。用已保存的提示词处理上面的样例。";
        else status = stale ? "上一版提示词或配置的结果 · 已过期" : "已保存版本的结果";
        if (!string.IsNullOrEmpty(_actionNotice)) status = _actionNotice;
        PreviewStatus.Text = status;

        if (failure != null)
        {
            PreviewFailure.Text = needsConfiguration
                ? failure.Explanation
                : failure.Category == ProcessingFailureCategory.Configuration
                    ? "配置或提示词已更新，可以重新试运行。"
                    : failure.Explanation;
            PreviewFailure.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewFailure.Text = "";
            PreviewFailure.Visibility = Visibility.Collapsed;
        }

        // Current streaming/partial text and the previous complete result have separate homes:
        // the attempt replaces the main area while it exists, and the old preview stays visible
        // as a labeled reference whenever a draft or a trial makes the current text differ.
        var mainText = attempt?.Preview.Text ?? (dirty ? "" : preview?.Text ?? "");
        var showMain = !dirty && mainText.Length > 0;
        PreviewResult.Visibility = showMain ? Visibility.Visible : Visibility.Collapsed;
        if (showMain) PreviewResult.Text = mainText;

        var showReference = preview != null && (dirty || attempt != null);
        PreviewReferenceLabel.Visibility = showReference ? Visibility.Visible : Visibility.Collapsed;
        PreviewReference.Visibility = showReference ? Visibility.Visible : Visibility.Collapsed;
        if (showReference)
        {
            PreviewReferenceLabel.Text = stale ? "参照 · 上一版提示词或配置的结果 · 已过期" : "参照 · 已保存版本的结果";
            PreviewReference.Text = preview!.Text;
        }
    }

    // MARK: shortcuts (#78)

    private void BuildShortcutRows()
    {
        // The label column takes the available width (title plus the one-line explanation); the
        // recorder and its 恢复默认 button keep their intrinsic width so a long custom
        // combination such as Ctrl+Alt+Shift+Win+Space never truncates its explanation.
        ShortcutRows.ColumnDefinitions.Add(new ColumnDefinition());
        ShortcutRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ShortcutRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        (GlobalShortcutAction Action, string Title, string Caption)[] rows =
        [
            (GlobalShortcutAction.ShowPanel, "打开面板", "选中文字会带入，按首项动作处理"),
            (GlobalShortcutAction.CaptureText, "截图翻译", "框选屏幕文字并翻译"),
            (GlobalShortcutAction.TranslationLayer, "段落原处翻译", "指着的段落换成译文，再按换回；加 Shift 翻译整个窗口"),
            (GlobalShortcutAction.ImproveAndReplace, "改进并替换", "改进选中文字，直接写回原处"),
        ];
        for (var index = 0; index < rows.Length; index++)
        {
            var (action, title, caption) = rows[index]; _shortcuts[action] = _baseline.ShortcutFor(action);
            ShortcutRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var explanation = new TextBlock
            {
                Text = caption, FontSize = 12, Foreground = (Brush)FindResource("InkSecondary"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 16, 0),
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
            text.Children.Add(label); text.Children.Add(explanation);
            var editor = new TextBox
            {
                IsReadOnly = true, MinWidth = 140, MaxWidth = 320, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "点一下，按组合键录入 · Backspace 清除",
            };
            editor.PreviewKeyDown += (_, e) => RecordShortcut(action, e);
            System.Windows.Automation.AutomationProperties.SetName(editor, title + "快捷键");
            _shortcutEditors[action] = editor;
            var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
            reset.Click += (_, _) => ResetShortcut(action);
            _shortcutResetButtons[action] = reset;
            Grid.SetRow(text, index); Grid.SetRow(editor, index); Grid.SetColumn(editor, 1);
            Grid.SetRow(reset, index); Grid.SetColumn(reset, 2);
            ShortcutRows.Children.Add(text); ShortcutRows.Children.Add(editor); ShortcutRows.Children.Add(reset);
        }
        ShortcutRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var derived = new TextBlock { Name = "WholeShortcut", Foreground = (Brush)FindResource("InkSecondary"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(derived, rows.Length); Grid.SetColumnSpan(derived, 3); ShortcutRows.Children.Add(derived); RefreshShortcutTexts();
    }
    private void RecordShortcut(GlobalShortcutAction action, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { _shortcuts[action] = null; RefreshShortcutTexts(); return; }
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= ShortcutModifiers.Win;
        if ((modifiers & GlobalShortcut.Required) == 0) { ShortcutStatus.Text = "请同时按 Ctrl、Alt 或 Win。"; return; }
        var proposed = new GlobalShortcut((ushort)KeyInterop.VirtualKeyFromKey(key), modifiers);
        var previous = _shortcuts[action]; _shortcuts[action] = proposed;
        try { _ = Draft(); ShortcutStatus.Text = "保存时会检查其他程序是否占用。"; }
        catch (Exception error) { _shortcuts[action] = previous; ShortcutStatus.Text = error.Message; }
        RefreshShortcutTexts();
    }
    private void ResetShortcut(GlobalShortcutAction action)
    {
        var previous = _shortcuts[action];
        _shortcuts[action] = DefaultShortcutFor(action);
        try { _ = Draft(); ShortcutStatus.Text = "已恢复默认组合。"; }
        catch (Exception error) { _shortcuts[action] = previous; ShortcutStatus.Text = error.Message; }
        RefreshShortcutTexts();
    }
    /// <summary>The combination a fresh configuration holds; the settings defaults cover every action.</summary>
    private static GlobalShortcut? DefaultShortcutFor(GlobalShortcutAction action) => DefaultSettings.ShortcutFor(action);
    private static readonly CidaSettings DefaultSettings = new();
    private void RefreshShortcutTexts()
    {
        foreach (var (action, editor) in _shortcutEditors)
        {
            editor.Text = _shortcuts[action]?.DisplayText ?? "未设置";
            if (_shortcutResetButtons.TryGetValue(action, out var reset))
                reset.Visibility = _shortcuts[action] != DefaultShortcutFor(action) ? Visibility.Visible : Visibility.Collapsed;
        }
        var derived = ShortcutRows.Children.OfType<TextBlock>().First(t => t.Name == "WholeShortcut");
        derived.Text = "整窗原处翻译：" + (_shortcuts[GlobalShortcutAction.TranslationLayer]?.AddingShift().DisplayText ?? "未设置") + "（段落快捷键加 Shift）";
    }
    private void OnResetShortcuts(object sender, RoutedEventArgs e)
    { foreach (var action in _shortcuts.Keys.ToList()) _shortcuts[action] = DefaultShortcutFor(action); RefreshShortcutTexts(); ShortcutStatus.Text = ""; }
    private void RefreshShortcutCapabilities()
    {
        if (ShortcutCapabilities == null) return;
        var failures = _model.HotkeyFailures;
        var status = failures.Count == 0 ? "当前：全局快捷键均已注册。" : "当前：" + string.Join("；", failures) + "。";
        ShortcutCapabilities.Text =
            "读取与替换：通过 Windows UI Automation 带入前台窗口的选中文字；原处翻译改写只读段落，改进并替换改写可编辑选区，无法安全替换时结果会留在面板里。\n"
            + "屏幕文字识别：先使用 Windows 内置 OCR；不可用时使用随安装包附带的 Tesseract 引擎（简体中文、繁体中文、英文），离线运行。\n"
            + "全局快捷键：由 Windows 全局注册；被其他程序占用时无法注册，对应操作仍可在面板内完成。\n"
            + status;
    }

    // MARK: updates (#79)

    private void OnUpdatesChanged() { if (!_closed) Dispatcher.BeginInvoke(new Action(RefreshUpdates)); }
    private void RefreshUpdates()
    {
        if (_closed) return;
        var updates = _model.Updates;
        var phase = updates.Phase;
        var dismissed = _dismissedUpdatePhase == phase;
        UpdateStatus.Text = updates.Status;
        var checking = phase == UpdatePhase.Checking;
        var downloading = phase == UpdatePhase.Downloading;
        var installing = phase == UpdatePhase.Installing;
        var found = phase == UpdatePhase.Found && !dismissed;
        var ready = phase == UpdatePhase.Ready && !dismissed;
        var failed = phase == UpdatePhase.Failed;
        CheckUpdate.Content = failed ? "重试" : "检查更新";
        CheckUpdate.Visibility = checking || downloading || installing || found || ready ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdate.IsEnabled = !checking && !downloading && !installing;
        DownloadUpdate.Visibility = found && updates.CanInstallNow ? Visibility.Visible : Visibility.Collapsed;
        RestartUpdate.Visibility = ready && updates.CanInstallNow ? Visibility.Visible : Visibility.Collapsed;
        UpdateLater.Visibility = (found || ready) ? Visibility.Visible : Visibility.Collapsed;
        SkipUpdate.Visibility = updates.CanSkipVersion && !dismissed ? Visibility.Visible : Visibility.Collapsed;
        CancelDownload.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        OpenReleases.Visibility = installing ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        // The session reports progress directly, so the bar does not depend on the status wording.
        var percent = downloading ? updates.DownloadPercent : null;
        UpdateProgress.IsIndeterminate = percent == null;
        if (percent is { } value) UpdateProgress.Value = value;
        var notes = updates.Notes;
        UpdateNotes.Text = notes.Count == 0 ? "" : "更新内容\n" + string.Join("\n", notes.Select(note => "· " + note));
        UpdateNotes.Visibility = notes.Count > 0 && (found || ready) ? Visibility.Visible : Visibility.Collapsed;
        UpdateHint.Visibility = found || downloading || ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateHint.Text = found ? "安装只需确认这一次；下载完成后会自动重启。"
            : downloading ? "下载会继续；离开设置页不会自动重启。"
            : "下载已完成，不会自动重启；点「安装并重启」完成安装。";
        UpdateDeferredNote.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void OnUpdateCheck(object sender, RoutedEventArgs e)
    {
        _dismissedUpdatePhase = null;
        await _model.Updates.CheckAsync();
    }
    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            // One confirmation: the service downloads with the install intent and restarts when
            // it finishes. AppModel injects Updates.PrepareForRestart, so panel work stops only
            // right before the updater replaces the build.
            await _model.Updates.InstallAndRestartAsync();
        }
        catch { UpdateStatus.Text = "无法安装更新，请从下载页重新安装。"; }
    }
    private void OnUpdateLater(object sender, RoutedEventArgs e) { _dismissedUpdatePhase = _model.Updates.Phase; RefreshUpdates(); }
    private void OnSkipUpdate(object sender, RoutedEventArgs e) => _model.Updates.SkipVersion();
    private void OnCancelDownload(object sender, RoutedEventArgs e) => _model.Updates.CancelDownload();

    // MARK: capabilities

    private async Task RefreshCapabilitiesAsync()
    {
        Capabilities.Text = "正在检查本地 OCR 与快捷键…";
        var description = await Task.Run(() => LocalOcr.CapabilitySummary());
        if (_closed) return;
        Capabilities.Text = description + "\n" + (_model.HotkeyFailures.Count == 0 ? "全局快捷键已注册。" : string.Join("\n", _model.HotkeyFailures));
        RefreshShortcutCapabilities();
    }
    private async void OnCapabilities(object sender, RoutedEventArgs e) => await RefreshCapabilitiesAsync();
    private static void Open(string address) { try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); } catch { } }
    private void OnReleases(object sender, RoutedEventArgs e) => Open("https://github.com/Oerroot/cida-win/releases");
    private void OnFeedback(object sender, RoutedEventArgs e) => Open("https://github.com/Oerroot/cida-win/issues");
    private void OnUpstream(object sender, RoutedEventArgs e) => Open("https://github.com/Xuanwo/cida");
    private static string ConfigurationDirectory => Environment.GetEnvironmentVariable("CIDA_PROFILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cida");
    private void OnOpenDirectory(object sender, RoutedEventArgs e) { Directory.CreateDirectory(ConfigurationDirectory); Open(ConfigurationDirectory); }
    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(ConfigurationDirectory, "settings.json.bak");
            if (!File.Exists(path)) { SaveStatus.Text = "没有可用的配置备份。"; return; }
            _model.SaveSettings(CidaSettings.FromJsonText(File.ReadAllText(path))); Close();
        }
        catch { SaveStatus.Text = "备份恢复失败，当前配置未改变。"; }
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var draft = Draft(); var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "cida-settings.json", Filter = "JSON 配置|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, draft.ToJsonText()); SaveStatus.Text = "配置已导出，不包含 API Key。";
        }
        catch { SaveStatus.Text = "导出失败，请检查草稿和保存位置。"; }
    }
}
