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
    private readonly ScreenCapturer _capturer = new();
    private readonly LocalOcr _ocr = new();
    private readonly ModelServiceClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private PanelWindow? _panel;
    private CaptureWindow? _capture;
    private SettingsWindow? _settingsWindow;
    private readonly TranslationLayer _layer = new();
    private LayerOverlayWindow? _layerOverlay;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CidaSettings _settings;
    private int _resultSerial;

    public AppModel()
    {
        _store = Cida.Platform.PlatformConfiguration.Production();
        _settings = _store.LoadSettings() with { ApiKey = _store.ReadApiKey() ?? "" };
        _client = new ModelServiceClient();
    }

    public void Start()
    {
        Task.Run(() =>
        {
            // Package identity unlocks Windows.Media.Ocr; the app runs fine without it
            // (capture reports OCR unavailable).
            new SparsePackageRegistrar().EnsureRegistered();
        });
        InstallTray();
        ApplyShortcuts();
        ListenForConfigurationChanges();
        WarmUpOcr();
    }

    public void Stop()
    {
        _shutdown.Cancel();
        _hotkeyPressedSubscription = null;
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
        _hotkeys.UnregisterAll();
        Register(_settings.Shortcut, GlobalHotkeySource.GlobalShortcutActionMirror.ShowPanel);
        Register(_settings.CaptureShortcut, GlobalHotkeySource.GlobalShortcutActionMirror.CaptureText);
        Register(_settings.LayerShortcut, GlobalHotkeySource.GlobalShortcutActionMirror.TranslationLayer);
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
        }
    }

    private Action<GlobalHotkeySource.GlobalShortcutActionMirror>? _hotkeyPressedSubscription;

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
                    ToggleLayer();
                    break;
            }
        });
    }

    private static System.Windows.Threading.Dispatcher Dispatcher => System.Windows.Application.Current.Dispatcher;

    // MARK: panel

    private void TogglePanel()
    {
        if (_panel == null || !_panel.IsLoaded)
        {
            _panel = new PanelWindow(this);
            _panel.Show();
            return;
        }
        if (_panel.IsVisible)
        {
            _panel.Hide();
            return;
        }
        _panel.Show();
        _panel.Activate();
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Show();
        }
        else
        {
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
        var result = await Task.Run(() => _selectionReader.Read(window));
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
    }

    public ConfigurationStore Store => _store;

    /// <summary>Sends a processing request and streams the reply to the panel.</summary>
    public async void Submit(PanelWindow panel, string text, ProcessingMode mode)
    {
        var serial = ++_resultSerial;
        panel.BeginResult(mode, text);
        if (!_settings.IsModelServiceComplete)
        {
            panel.FailResult("还没配置模型服务，右键托盘图标打开设置。");
            return;
        }
        var languages = _settings.RequestLanguages();
        var request = new ProcessingRequest
        {
            Text = text,
            Mode = mode,
            MyLanguage = languages.My,
            ForeignLanguage = languages.Foreign,
        };
        var cancellation = new CancellationTokenSource();
        panel.BindCancellation(cancellation);
        try
        {
            var prepared = ModelServiceClient.Prepare(request, _settings);
            await _client.SendAsync(
                prepared,
                _settings.ModelService.Format,
                new SecretRedactor(_settings.ApiKey),
                null,
                piece =>
                {
                    if (serial == _resultSerial)
                    {
                        Dispatcher.BeginInvoke(() => panel.AppendResult(piece));
                    }
                },
                cancellation.Token);
            Dispatcher.BeginInvoke(() =>
            {
                if (serial == _resultSerial) panel.CompleteResult();
            });
        }
        catch (ModelServiceException error)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (serial == _resultSerial) panel.FailResult(error.Error.Description());
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (serial == _resultSerial) panel.StopResult();
            });
        }
    }

    // MARK: capture

    private void StartCapture()
    {
        if (_capture != null && _capture.IsLoaded) return;
        _capture = new CaptureWindow(this, _capturer, _ocr);
        _capture.Show();
    }

    /// <summary>The capture window calls this with the recognized text.</summary>
    public void SubmitCapture(string text)
    {
        TogglePanel();
        if (_panel != null)
        {
            Submit(_panel, text, ProcessingMode.Translate);
        }
    }

    // MARK: translation layer (pointer paragraph)

    private void ToggleLayer()
    {
        if (_layerOverlay != null)
        {
            _layerOverlay.Close();
            _layerOverlay = null;
            return;
        }
        var paragraph = Task.Run(() => _layer.ParagraphUnderCursor()).Result;
        if (paragraph == null)
        {
            // No readable paragraph under the pointer: the panel remains the fallback.
            TogglePanel();
            return;
        }
        TranslateInPlace(paragraph);
    }

    private void TranslateInPlace(TranslationLayer.LayerParagraph paragraph)
    {
        if (!_settings.IsModelServiceComplete)
        {
            TogglePanel();
            return;
        }
        var overlay = new LayerOverlayWindow("", paragraph.Bounds);
        _layerOverlay = overlay;
        overlay.Show();

        var languages = _settings.RequestLanguages();
        var request = new ProcessingRequest
        {
            Text = paragraph.Text,
            Mode = ProcessingMode.Translate,
            MyLanguage = languages.My,
            ForeignLanguage = languages.Foreign,
        };
        var serial = _resultSerial;
        Task.Run(async () =>
        {
            try
            {
                var prepared = ModelServiceClient.Prepare(request, _settings);
                await _client.SendAsync(
                    prepared,
                    _settings.ModelService.Format,
                    new SecretRedactor(_settings.ApiKey),
                    null,
                    piece => Dispatcher.BeginInvoke(() =>
                    {
                        if (_layerOverlay == overlay) overlay.Append(piece);
                    }));
            }
            catch (ModelServiceException error)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_layerOverlay == overlay)
                    {
                        overlay.Close();
                        _layerOverlay = null;
                        TogglePanel();
                    }
                });
                _ = error;
            }
        });
    }

    // MARK: tray

    private void InstallTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => TogglePanel());
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
        _tray.DoubleClick += (_, _) => TogglePanel();
    }

    private void SetTrayTooltip(string text)
    {
        if (_tray != null) _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private static System.Drawing.Icon TrayIcon()
    {
        // A rounded "辞" mark drawn at runtime: no icon file to ship.
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xB4, 0x55, 0x2D));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var foreground = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        using var font = new System.Drawing.Font("Microsoft YaHei UI", 13f, System.Drawing.FontStyle.Bold);
        var size = graphics.MeasureString("辞", font);
        graphics.DrawString("辞", font, foreground, (32 - size.Width) / 2, (32 - size.Height) / 2);
        System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        return icon;
    }

    // MARK: configuration changes (CLI ↔ GUI)

    private void ListenForConfigurationChanges()
    {
        Cida.Platform.ConfigurationChangeNotifier.Listen(
            () => Dispatcher.BeginInvoke(ReloadSettings),
            _shutdown.Token);
    }
}
