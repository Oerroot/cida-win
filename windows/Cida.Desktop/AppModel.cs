using Cida.Core;
using Cida.Platform;
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;



namespace Cida.Desktop;

/// <summary>
/// The application model: owns the settings, the shortcuts, the panel window and the tray
/// icon. Ported in shape from upstream AppModel.swift/AppLifecycle.swift.
/// </summary>
public sealed class AppModel
{
    private readonly ConfigurationStore _store;
    private readonly GlobalHotkeySource _hotkeys = new();
    private readonly SelectionReader _selectionReader = new();
    private readonly AccessibilityWorker _accessibility = new();
    private readonly ScreenCapturer _capturer = new();
    private readonly LocalOcr _ocr = new();
    private readonly ModelServiceClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private PanelWindow? _panel;
    private CaptureWindow? _capture;
    private bool _capturePending;
    private SettingsWindow? _settingsWindow;
    private readonly TranslationLayer _layer = new();
    private LayerOverlayWindow? _layerOverlay;
    private CancellationTokenSource? _layerCancellation;
    private LayerSession? _layerSession;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CidaSettings _settings;
    private int _resultSerial;
    private CancellationTokenSource? _replaceCancellation;
    private CancellationTokenSource? _recognitionCancellation;
    public bool IsSettingsVisible => _settingsWindow?.IsVisible == true;
    public UpdateService Updates { get; } = new();
    private readonly List<string> _hotkeyFailures = [];
    public IReadOnlyList<string> HotkeyFailures => _hotkeyFailures;

    public AppModel(ConfigurationStore? store = null)
    {
        _store = store ?? Cida.Platform.PlatformConfiguration.Production(Environment.GetEnvironmentVariable("CIDA_PROFILE"));
        _settings = _store.LoadSettings() with { ApiKey = _store.ReadApiKey() ?? "" };
        _client = new ModelServiceClient();
    }

    public void Start()
    {
        ThemeService.Start();
        DevelopmentTrace.Stage("theme ready");
        InstallTray();
        DevelopmentTrace.Stage("tray ready");
        ApplyShortcuts();
        ListenForConfigurationChanges();
        WarmUpOcr();
        _ = Updates.CheckDailyAsync();
        if (!_settings.IsModelServiceComplete) ShowPanel(readSelection: false);
        DevelopmentTrace.Stage("start complete modelConfigured=" + _settings.IsModelServiceComplete);
    }

    public void Stop()
    {
        _shutdown.Cancel();
        Updates.CancelDownload();
        _accessibility.Dispose();
        _replaceCancellation?.Cancel();
        _recognitionCancellation?.Cancel();
        ThemeService.Stop();
        _layerCancellation?.Cancel();
        _layerOverlay?.Close();
        _layerSession?.Dispose();
        _hotkeys.Dispose();
        _panel?.Close();
        _capture?.Close();
        _settingsWindow?.Close();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }

    private void WarmUpOcr()
    {
        Task.Run(() =>
        {
            try
            {
                _ = LocalOcr.AvailableLanguageTags();
            }
            catch
            {
                // OCR availability is checked again when a capture runs.
            }
        });
    }

    // MARK: shortcuts

    private void ApplyShortcuts()
    {
        _hotkeyFailures.Clear();
        _hotkeys.UnregisterAll();
        Register(_settings.Shortcut, GlobalHotkeySource.GlobalShortcutActionMirror.ShowPanel);
        Register(_settings.CaptureShortcut, GlobalHotkeySource.GlobalShortcutActionMirror.CaptureText);
        Register(_settings.LayerShortcut, GlobalHotkeySource.GlobalShortcutActionMirror.TranslationLayer);
        Register(_settings.LayerShortcut?.AddingShift(), GlobalHotkeySource.GlobalShortcutActionMirror.WholeWindowTranslationLayer);
        Register(_settings.ImproveShortcut, GlobalHotkeySource.GlobalShortcutActionMirror.ImproveAndReplace);
        _hotkeys.HotkeyPressed -= OnHotkey;
        _hotkeys.HotkeyPressed += OnHotkey;
    }

    private void Register(GlobalShortcut? shortcut, GlobalHotkeySource.GlobalShortcutActionMirror action)
    {
        if (shortcut == null) return;
        if (!_hotkeys.Register(shortcut.Value.KeyCode, (byte)shortcut.Value.Modifiers, action))
        {
            // A conflict: keep the previous registration (there is none for this action) and
            // surface it in the tray tooltip.
            SetTrayTooltip($"辞达：快捷键 {shortcut.Value.DisplayText} 被其他程序占用");
            _hotkeyFailures.Add($"{shortcut.Value.DisplayText} 被其他程序占用");
        }
    }

    private void OnHotkey(GlobalHotkeySource.GlobalShortcutActionMirror action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (action)
            {
                case GlobalHotkeySource.GlobalShortcutActionMirror.ShowPanel:
                    TogglePanel();
                    break;
                case GlobalHotkeySource.GlobalShortcutActionMirror.CaptureText:
                    StartCapture();
                    break;
                case GlobalHotkeySource.GlobalShortcutActionMirror.TranslationLayer:
                    ToggleLayer(false);
                    break;
                case GlobalHotkeySource.GlobalShortcutActionMirror.WholeWindowTranslationLayer:
                    ToggleLayer(true);
                    break;
                case GlobalHotkeySource.GlobalShortcutActionMirror.ImproveAndReplace:
                    ImproveAndReplace();
                    break;
            }
        });
    }

    private static System.Windows.Threading.Dispatcher Dispatcher => System.Windows.Application.Current.Dispatcher;

    // MARK: panel

    private void TogglePanel()
    {
        if (_panel?.IsVisible == true)
        {
            _panel.Hide();
            return;
        }
        RememberSourceWindow();
        ShowPanel(readSelection: true);
    }

    private void ShowPanel(bool readSelection)
    {
        if (_panel == null || !_panel.IsLoaded)
        {
            _panel = new PanelWindow(this);
            _panel.Show();
        }
        else _panel.Show();
        if (readSelection) _ = _panel.BringInSelectionAsync();
        else _panel.FocusInput();
    }
    public void OpenPanel() => ShowPanel(readSelection: false);

    public void ShowSettings()
    {
        _panel?.Hide();
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
    }

    public void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }

    // MARK: reading the selection

    /// <summary>The frontmost window before the panel takes focus, captured by the hotkey.</summary>
    public nint SourceWindow { get; private set; }

    public void RememberSourceWindow()
    {
        SourceWindow = Cida.Platform.SelectionReader.ForegroundWindow();
    }

    public async Task<PanelWindow.Submission?> ReadSelectionAsync()
    {
        var window = SourceWindow;
        if (window == 0) return null;
        var response = await _accessibility.QueryAsync(new("selection", (long)window), _shutdown.Token);
        var result = response?.Selection;
        if (result?.Kind == SelectionReader.Result.OutcomeKind.Unreadable && SelectionReader.ForegroundWindow() == window)
            result = await Task.Run(() => _selectionReader.ReadByCopying(window, _shutdown.Token));
        if (result == null) return null;
        return result.Text is { Length: > 0 } text
            ? new PanelWindow.Submission(text, _selectionReader /* strategy kept for notes */)
            : null;
    }

    // MARK: requests

    public CidaSettings Settings => _settings;

    public void ReloadSettings()
    {
        _settings = _store.LoadSettings() with { ApiKey = _store.ReadApiKey() ?? "" };
        ApplyShortcuts();
        _panel?.RefreshConfiguration();
        _layerSession?.Dispose();
        _layerOverlay?.Close(); _layerOverlay = null;
        _layerCancellation?.Cancel();
    }
    public void SaveSettings(CidaSettings settings)
    {
        if (!settings.HasValidShortcuts) throw new InvalidOperationException("快捷键重复，或段落快捷键包含 Shift。");
        var previous = _settings;
        _settings = settings; ApplyShortcuts();
        if (_hotkeyFailures.Count > 0)
        {
            var message = string.Join("；", _hotkeyFailures); _settings = previous; ApplyShortcuts();
            throw new InvalidOperationException(message + "，请重新设置快捷键。");
        }
        try { _store.SaveSettings(settings); }
        catch { _settings = previous; ApplyShortcuts(); throw; }
        ReloadSettings(); _store.NotifyChange();
    }
    public void PrepareForUpdate()
    { _panel?.StopResult(); _replaceCancellation?.Cancel(); _layerCancellation?.Cancel(); _layerSession?.Dispose(); }
    public string RequestFingerprint(ProcessingAction action) => _settings.ModelServiceFingerprint + "|" +
        _settings.MyLanguage + "|" + _settings.ForeignLanguage + "|" + action.Id + "|" + action.Prompt;

    public ConfigurationStore Store => _store;

    /// <summary>Sends a processing request and streams the reply to the panel.</summary>
    public async void Submit(PanelWindow panel, string text, ProcessingAction action)
    {
        var serial = ++_resultSerial;
        panel.BeginResult(action, text);
        if (!_settings.IsModelServiceComplete)
        {
            panel.FailResult("请先打开设置，配置模型服务。");
            return;
        }
        var settings = _settings;
        var languages = settings.RequestLanguages();
        var request = new ProcessingRequest
        {
            Text = text,
            Mode = action.Mode,
            ActionPrompt = action.Prompt,
            MyLanguage = languages.My,
            ForeignLanguage = languages.Foreign,
        };
        var cancellation = new CancellationTokenSource();
        panel.BindCancellation(cancellation);
        try
        {
            var prepared = ModelServiceClient.Prepare(request, settings);
            await _client.SendAsync(
                prepared,
                settings.ModelService.Format,
                new SecretRedactor(settings.ApiKey),
                null,
                piece => Dispatcher.BeginInvoke(() =>
                {
                    if (serial == _resultSerial && !cancellation.IsCancellationRequested) panel.AppendResult(piece);
                }),
                cancellation.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (serial == _resultSerial) panel.CompleteResult();
            });
        }
        catch (ModelServiceException error)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (serial == _resultSerial) panel.FailResult(error.Error.Description());
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (serial == _resultSerial) panel.StopResult();
            });
        }
        catch (Exception)
        {
            if (serial == _resultSerial) panel.FailResult("请求未能完成，请检查连接后重试。");
        }
        finally { panel.UnbindCancellation(cancellation); cancellation.Dispose(); }
    }

    // MARK: capture

    public async void StartCapture()
    {
        _recognitionCancellation?.Cancel(); _panel?.StopResult();
        if (_capturePending || (_capture != null && _capture.IsLoaded)) return;
        _capturePending = true;
        _panel?.Hide(); _settingsWindow?.Hide();
        _layerSession?.Dispose(); _layerOverlay?.Close(); _layerOverlay = null;
        try { await Task.Delay(80, _shutdown.Token); _capture = new CaptureWindow(this, _capturer, _ocr); _capture.Show(); }
        catch (OperationCanceledException) { }
        catch (Exception) { OpenPanel(); _panel?.ShowMessage("无法截取屏幕，请重试。", true); }
        finally { _capturePending = false; }
    }

    /// <summary>The capture window calls this with the recognized text.</summary>
    public void SubmitCapture(string text)
    {
        ShowPanel(readSelection: false);
        if (_panel != null)
        {
            _panel.SetSourceText(text);
            _panel.UseTranslation();
            Submit(_panel, text, _settings.EffectiveActions.First(a => a.Id == ProcessingAction.TranslationId));
        }
    }
    public void CaptureStatus(string? error = null)
    {
        OpenPanel(); _panel?.ShowRecognitionState(error);
    }
    public void StopRecognition() => _recognitionCancellation?.Cancel();
    public async Task RecognizeCaptureAsync(byte[] pixels, int width, int height)
    {
        _recognitionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _recognitionCancellation = cancellation;
        CaptureStatus();
        try
        {
            var language = _settings.MyLanguage.Contains("繁") ? "zh-Hant" : "zh-Hans";
            var lines = await _ocr.RecognizeAsync(pixels, width, height, language, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_recognitionCancellation, cancellation)) return;
            if (lines == null || lines.Count == 0) { CaptureStatus("截图中没有识别到文字，请重新框选。" ); return; }
            SubmitCapture(string.Join("\n\n", RecognizedLayout.Paragraphs(lines).Select(p => p.Text)));
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_recognitionCancellation, cancellation)) _panel?.ShowMessage("文字识别已停止，可重新截图。", true); }
        catch { CaptureStatus("本地文字识别失败，请在设置中检查能力或重新截图。"); }
        finally { if (ReferenceEquals(_recognitionCancellation, cancellation)) { _recognitionCancellation = null; _panel?.FinishRecognition(); } cancellation.Dispose(); }
    }

    // MARK: translation layer (pointer paragraph)

    private async void ToggleLayer(bool wholeWindow)
    {
        if (!wholeWindow && _layerSession is { IsParagraph: false }) { _layerSession.ToggleOriginal(); return; }
        var wasSameLayer = _layerSession?.IsParagraph == !wholeWindow || (!wholeWindow && (_layerOverlay != null || _layerCancellation != null));
        // The whole-window variant (Alt+Shift+D) and the paragraph variant close each other.
        _layerOverlay?.Close();
        _layerOverlay = null;
        _layerCancellation?.Cancel();
        _layerCancellation = null;
        _layerSession?.Dispose();
        if (wasSameLayer) return;
        if (!_settings.IsModelServiceComplete) { OpenPanel(); _panel?.ShowMessage("请先配置模型服务，再使用原处翻译。"); return; }
        if (!wholeWindow)
        {
            var cancellation = new CancellationTokenSource();
            _layerCancellation = cancellation;
            var point = System.Windows.Forms.Cursor.Position;
            var owner = SelectionReader.ForegroundWindow();
            AccessibilityResponse? response;
            try { response = await _accessibility.QueryAsync(new("paragraph", (long)owner, point.X, point.Y), cancellation.Token); }
            catch (OperationCanceledException) { cancellation.Dispose(); return; }
            var paragraph = response?.Paragraph;
            if (cancellation.IsCancellationRequested || SelectionReader.ForegroundWindow() != owner) { _layerCancellation = null; cancellation.Dispose(); return; }
            if (paragraph == null)
            {
                _layerCancellation = null;
                cancellation.Dispose();
                OpenPanel(); _panel?.ShowMessage("当前位置没有可读取的段落，可使用截图翻译。", true);
                return;
            }
            _layerCancellation = null; cancellation.Dispose();
            _layerSession = new LayerSession(this, owner, paragraph); _layerSession.Start();
            return;
        }
        var window = Cida.Platform.WindowParagraphReader.ForegroundWindow();
        if (window == 0)
        {
            TogglePanel();
            return;
        }
        _layerSession = new LayerSession(this, window);
        _layerSession.Start();
    }
    private async void ImproveAndReplace()
    {
        if (_replaceCancellation != null) { _replaceCancellation.Cancel(); return; }
        var window = SelectionReader.ForegroundWindow();
        if (window == 0 || !_settings.IsModelServiceComplete) { OpenPanel(); _panel?.ShowMessage("请先配置模型服务，再使用改进并替换。"); return; }
        var cancellation = new CancellationTokenSource(); _replaceCancellation = cancellation;
        try
        {
            var response = await _accessibility.QueryAsync(new("replacement", (long)window), cancellation.Token);
            var source = response?.Replacement;
            if (source == null || !source.Editable)
            { RememberSourceWindow(); ShowPanel(true); _panel?.ShowMessage("此选区无法安全替换，可在面板中改进后复制。"); return; }
            var settings = _settings; var languages = settings.RequestLanguages();
            var request = new ProcessingRequest { Text = source.Text, Mode = ProcessingMode.Improve, ActionPrompt = settings.ImprovementPrompt, MyLanguage = languages.My, ForeignLanguage = languages.Foreign };
            var result = new System.Text.StringBuilder();
            SetTrayTooltip("辞达：正在改进选区，再按快捷键可取消");
            await _client.SendAsync(ModelServiceClient.Prepare(request, settings), settings.ModelService.Format, new SecretRedactor(settings.ApiKey), null, piece => result.Append(piece), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var text = result.ToString();
            var replaced = await SelectionReplacer.ReplaceAsync(source, text, _accessibility, cancellation.Token);
            if (!replaced)
            {
                OpenPanel();
                var action = new ProcessingAction { Id = ProcessingAction.ImprovementId, Name = "改进", Prompt = settings.ImprovementPrompt };
                _panel!.RetainResult(action, source.Text, text, "无法确认原处替换已完成。结果已保留，请先检查原文，再决定是否复制。");
            }
        }
        catch (OperationCanceledException) { }
        catch (ModelServiceException error) { OpenPanel(); _panel?.ShowMessage("改进失败：" + error.Error.Description()); }
        catch { OpenPanel(); _panel?.ShowMessage("无法完成安全替换，请在面板中处理后复制。"); }
        finally
        { if (ReferenceEquals(_replaceCancellation, cancellation)) _replaceCancellation = null; cancellation.Dispose(); SetTrayTooltip("辞达：翻译与润色"); }
    }

    internal void LayerSessionEnded(LayerSession session)
    {
        if (ReferenceEquals(_layerSession, session))
        {
            _layerSession = null;
        }
    }
    internal void RetainLayerResult(string source, string result, string note)
    {
        OpenPanel(); _panel!.SetSourceText(source); _panel.UseTranslation();
        var action = _settings.EffectiveActions.First(a => a.Id == ProcessingAction.TranslationId);
        _panel.BeginResult(action, source); _panel.AppendResult(result); _panel.CompleteResult(); _panel.ShowMessage(note);
    }
    internal void TranslateLayerInPanel(string source)
    {
        OpenPanel(); _panel!.SetSourceText(source); _panel.UseTranslation();
        Submit(_panel, source, _settings.EffectiveActions.First(a => a.Id == ProcessingAction.TranslationId));
    }
    internal void LayerUnavailable() { OpenPanel(); _panel?.ShowMessage("当前窗口没有可跟踪的只读段落，可使用截图翻译。", true); }

    // MARK: tray

    private void InstallTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => OpenPanel());
        menu.Items.Add("截图翻译", null, (_, _) => StartCapture());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出辞达", null, (_, _) => Exit());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "辞达：翻译与润色",
            Icon = TrayIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenPanel();
    }

    private void SetTrayTooltip(string text)
    {
        if (_tray != null) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private static System.Drawing.Icon TrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Cida;component/Assets/Brand/Cida.ico")).Stream;
        return new System.Drawing.Icon(stream, 32, 32);
    }

    // MARK: configuration changes (CLI ↔ GUI)

    private void ListenForConfigurationChanges()
    {
        Cida.Platform.ConfigurationChangeNotifier.Listen(
            () => Dispatcher.BeginInvoke(ReloadSettings),
            _shutdown.Token);
    }
}
