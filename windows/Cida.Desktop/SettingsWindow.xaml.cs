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
    private bool _loadingAction;
    private int _editing = -1;
    private CancellationTokenSource? _checkCancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _closed;
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
        ActionsList.ItemsSource = _actions; ActionsList.SelectedIndex = 0;
        BuildShortcutRows(); RefreshUpdates();
        _model.Updates.Changed += OnUpdatesChanged;
        Closed += (_, _) => { _closed = true; _checkCancellation?.Cancel(); _previewCancellation?.Cancel(); _model.Updates.Changed -= OnUpdatesChanged; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += async (_, _) => await RefreshCapabilitiesAsync();
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
            System.Windows.Clipboard.SetText("请帮助我配置辞达 Cida Windows 的模型服务。询问完整端点、协议（chat-completions / responses / anthropic-messages）、模型名称与认证方式。API Key 不写入命令行或普通文件，使用 Cida.Cli.exe config set api-key --stdin。其余字段使用 Cida.Cli.exe config schema 查看，配置后执行 Cida.Cli.exe check。不要要求发送或展示密钥。");
            SaveStatus.Text = "配置说明已复制，可交给 AI 助手。";
        }
        catch { SaveStatus.Text = "剪贴板正忙，请重试。"; }
    }
    private void OnActionSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ActionName == null || ActionsList.SelectedIndex < 0) return;
        _editing = ActionsList.SelectedIndex; var action = _actions[_editing]; _loadingAction = true;
        ActionName.Text = action.Name; ActionPrompt.Text = action.Prompt; ActionEnabled.IsChecked = action.Enabled;
        ActionEnabled.IsEnabled = action.Id != ProcessingAction.TranslationId;
        DeleteAction.IsEnabled = action.Id != ProcessingAction.TranslationId;
        ResetPrompt.Visibility = action.Mode == ProcessingMode.Custom ? Visibility.Collapsed : Visibility.Visible;
        _loadingAction = false; PreviewResult.Text = "";
    }
    private void OnActionEdited(object sender, TextChangedEventArgs e) => EditAction();
    private void OnActionToggled(object sender, RoutedEventArgs e) => EditAction();
    private void EditAction()
    {
        if (_loadingAction || _editing < 0 || ActionPrompt == null) return;
        _actions[_editing] = _actions[_editing] with { Name = ActionName.Text, Prompt = ActionPrompt.Text, Enabled = ActionEnabled.IsChecked == true };
        ActionsList.Items.Refresh();
    }
    private void SelectActions(int index)
    { ActionsList.ItemsSource = null; ActionsList.ItemsSource = _actions; ActionsList.SelectedIndex = Math.Clamp(index, 0, _actions.Count - 1); }
    private void OnAddAction(object sender, RoutedEventArgs e)
    { _actions.Add(new ProcessingAction { Name = "新动作", Prompt = "Rewrite the user-provided text concisely. Return only the rewritten text." }); SelectActions(_actions.Count - 1); }
    private void OnDeleteAction(object sender, RoutedEventArgs e)
    { if (_editing < 0 || _actions[_editing].Id == ProcessingAction.TranslationId) return; var index = _editing; _actions.RemoveAt(index); _editing = -1; SelectActions(index); }
    private void MoveAction(int direction)
    {
        var index = _editing; var target = index + direction;
        if (index < 0 || target < 0 || target >= _actions.Count) return;
        (_actions[index], _actions[target]) = (_actions[target], _actions[index]); SelectActions(target);
    }
    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveAction(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveAction(1);
    private void OnResetPrompt(object sender, RoutedEventArgs e)
    { if (_editing >= 0 && _actions[_editing].Mode != ProcessingMode.Custom) ActionPrompt.Text = CidaSettings.DefaultPromptFor(_actions[_editing].Mode); }
    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_previewCancellation != null || _editing < 0) return;
        try
        {
            var draft = Draft(); var action = _actions[_editing]; var languages = draft.RequestLanguages();
            _previewCancellation = new(); PreviewButton.IsEnabled = false; CancelPreview.Visibility = Visibility.Visible; PreviewResult.Text = "正在预览…";
            var request = new ProcessingRequest { Text = "This sentence need to be more clear and natural.", Mode = action.Mode, ActionPrompt = action.Prompt, MyLanguage = languages.My, ForeignLanguage = languages.Foreign };
            var result = new System.Text.StringBuilder();
            await new ModelServiceClient().SendAsync(ModelServiceClient.Prepare(request, draft), draft.ModelService.Format,
                new SecretRedactor(draft.ApiKey), null, piece => result.Append(piece), _previewCancellation.Token);
            if (!_closed) PreviewResult.Text = result.ToString();
        }
        catch (OperationCanceledException) { PreviewResult.Text = "预览已停止"; }
        catch (ModelServiceException error) { PreviewResult.Text = "预览失败：" + error.Error.Description(); }
        catch (Exception error) { PreviewResult.Text = error is InvalidOperationException or InvalidConfiguration ? error.Message : "预览未能完成，请检查草稿配置。"; }
        finally { _previewCancellation?.Dispose(); _previewCancellation = null; PreviewButton.IsEnabled = true; CancelPreview.Visibility = Visibility.Collapsed; }
    }
    private void OnCancelPreview(object sender, RoutedEventArgs e) => _previewCancellation?.Cancel();
    private void BuildShortcutRows()
    {
        ShortcutRows.ColumnDefinitions.Add(new ColumnDefinition()); ShortcutRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        (GlobalShortcutAction Action, string Label)[] rows = [(GlobalShortcutAction.ShowPanel, "选中文字处理"), (GlobalShortcutAction.CaptureText, "截图翻译"), (GlobalShortcutAction.TranslationLayer, "段落原处翻译"), (GlobalShortcutAction.ImproveAndReplace, "改进并替换")];
        for (var index = 0; index < rows.Length; index++)
        {
            var (action, label) = rows[index]; _shortcuts[action] = _baseline.ShortcutFor(action);
            ShortcutRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 16, 12) };
            var editor = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 6, 0, 6), ToolTip = "按组合键录入 · Backspace 清除" };
            editor.PreviewKeyDown += (_, e) => RecordShortcut(action, e);
            System.Windows.Automation.AutomationProperties.SetName(editor, label + "快捷键");
            _shortcutEditors[action] = editor; Grid.SetRow(name, index); Grid.SetRow(editor, index); Grid.SetColumn(editor, 1);
            ShortcutRows.Children.Add(name); ShortcutRows.Children.Add(editor);
        }
        ShortcutRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var derived = new TextBlock { Name = "WholeShortcut", Foreground = (Brush)FindResource("InkSecondary"), FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(derived, rows.Length); Grid.SetColumnSpan(derived, 2); ShortcutRows.Children.Add(derived); RefreshShortcutTexts();
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
    private void RefreshShortcutTexts()
    {
        foreach (var (action, editor) in _shortcutEditors) editor.Text = _shortcuts[action]?.DisplayText ?? "未设置";
        var derived = ShortcutRows.Children.OfType<TextBlock>().First(t => t.Name == "WholeShortcut");
        derived.Text = "整窗原处翻译：" + (_shortcuts[GlobalShortcutAction.TranslationLayer]?.AddingShift().DisplayText ?? "未设置") + "（段落快捷键加 Shift）";
    }
    private void OnResetShortcuts(object sender, RoutedEventArgs e)
    { var defaults = new CidaSettings(); foreach (var action in _shortcuts.Keys.ToList()) _shortcuts[action] = defaults.ShortcutFor(action); RefreshShortcutTexts(); ShortcutStatus.Text = ""; }
    private void OnUpdatesChanged() { if (!_closed) Dispatcher.BeginInvoke(RefreshUpdates); }
    private void RefreshUpdates()
    { UpdateStatus.Text = _model.Updates.Status; DownloadUpdate.Visibility = _model.Updates.CanDownload ? Visibility.Visible : Visibility.Collapsed; RestartUpdate.Visibility = _model.Updates.CanRestart ? Visibility.Visible : Visibility.Collapsed; CancelDownload.Visibility = _model.Updates.IsDownloading ? Visibility.Visible : Visibility.Collapsed; }
    private async void OnUpdateCheck(object sender, RoutedEventArgs e) => await _model.Updates.CheckAsync();
    private async void OnDownloadUpdate(object sender, RoutedEventArgs e) => await _model.Updates.DownloadAsync();
    private void OnCancelDownload(object sender, RoutedEventArgs e) => _model.Updates.CancelDownload();
    private void OnRestartUpdate(object sender, RoutedEventArgs e)
    { _model.PrepareForUpdate(); try { _model.Updates.Restart(); } catch { UpdateStatus.Text = "无法安装更新，请从下载页重新安装。"; } }
    private async Task RefreshCapabilitiesAsync()
    {
        Capabilities.Text = "正在检查本地 OCR 与快捷键…";
        var description = await Task.Run(() => LocalOcr.CapabilitySummary());
        if (!_closed) Capabilities.Text = description + "\n" + (_model.HotkeyFailures.Count == 0 ? "全局快捷键已注册。" : string.Join("\n", _model.HotkeyFailures));
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
