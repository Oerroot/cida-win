using Cida.Core;
using Cida.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace Cida.Desktop;

public sealed class LayerSession : IDisposable
{
    private readonly AppModel _model;
    private readonly nint _window;
    private readonly AccessibilityWorker _reader = new();
    private readonly LayerTranslationCache _cache = new();
    private readonly Dictionary<string, LayerOverlayWindow> _overlays = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private IReadOnlyList<WindowParagraphReader.Paragraph> _last = [];
    private bool _disposed, _reading, _translating, _showOriginal;
    private long _revision;
    private DateTime _retryAfter;
    private DateTime _readAfter;
    private readonly WindowChangeWatch _watch;
    private readonly TranslationLayer.LayerParagraph? _target;
    private bool _firstRead = true;
    public bool IsParagraph => _target != null;
    public LayerSession(AppModel model, nint window, TranslationLayer.LayerParagraph? target = null)
    {
        _model = model; _window = window; _target = target;
        _watch = new WindowChangeWatch(window, () =>
        {
            if (_disposed) return;
            ++_revision; _readAfter = DateTime.UtcNow.AddMilliseconds(200);
            foreach (var overlay in _overlays.Values) overlay.Hide();
        });
        _clock.Tick += async (_, _) => await RefreshAsync();
    }
    public void Start() { _clock.Start(); _ = RefreshAsync(); }
    public void ToggleOriginal()
    {
        _showOriginal = !_showOriginal;
        foreach (var overlay in _overlays.Values) overlay.Hide();
        if (!_showOriginal) { ++_revision; _ = RefreshAsync(); }
    }
    private static string Identity(WindowParagraphReader.Paragraph p) => WindowParagraphReader.Key(p.RuntimeId) + "|" + p.Text;
    private string CacheKey(string text) => _model.RequestFingerprint(_model.Settings.EffectiveActions.First(a => a.Id == ProcessingAction.TranslationId)) + "|layer|" + text;
    private async Task RefreshAsync()
    {
        if (_disposed || _reading || DateTime.UtcNow < _readAfter) return;
        if (!IsWindow(_window)) { Dispose(); return; }
        if (SelectionReader.ForegroundWindow() != _window)
        { foreach (var overlay in _overlays.Values) overlay.Hide(); ++_revision; _last = []; return; }
        _reading = true;
        try
        {
            var readRevision = _revision;
            var response = await _reader.QueryAsync(new("window", (long)_window), _cancellation.Token);
            if (_disposed || readRevision != _revision || DateTime.UtcNow < _readAfter || SelectionReader.ForegroundWindow() != _window) return;
            IReadOnlyList<WindowParagraphReader.Paragraph> paragraphs = response?.Paragraphs ?? [];
            paragraphs = paragraphs.Where(p => p.Text == p.FullText).ToArray();
            if (_target != null)
            {
                paragraphs = paragraphs.Where(p => p.StartOffset == _target.StartOffset && p.RuntimeId.Length > 0 &&
                    p.RuntimeId.Take(p.RuntimeId.Length - 1).SequenceEqual(_target.RuntimeId ?? [])).ToArray();
                if (paragraphs.Any(p => p.FullText != _target.Text)) { Dispose(); return; }
                // A partially clipped paragraph is hidden until fully visible again.
                paragraphs = paragraphs.Where(p => p.Text == _target.Text).Take(1).ToArray();
            }
            if (_firstRead && paragraphs.Count == 0)
            {
                _firstRead = false; Dispose();
                if (_target != null) _model.TranslateLayerInPanel(_target.Text);
                else _model.LayerUnavailable();
                return;
            }
            _firstRead = false;
            var changed = paragraphs.Count != _last.Count || paragraphs.Zip(_last).Any(pair => pair.First.Text != pair.Second.Text || pair.First.Bounds != pair.Second.Bounds);
            if (changed)
            {
                ++_revision; _last = paragraphs;
                foreach (var overlay in _overlays.Values) overlay.Close(); _overlays.Clear();
            }
            PlaceCached(paragraphs);
            if (!_translating && DateTime.UtcNow >= _retryAfter)
            {
                var pending = paragraphs.Where(p => !_cache.TryGet(CacheKey(p.Text), out _) && (_target != null || ShouldTranslate(p.Text, _model.Settings.RequestLanguages().My))).Select(p => p.Text).Distinct().Take(12).ToList();
                if (pending.Count > 0) _ = TranslateAsync(pending, _revision);
            }
        }
        catch (OperationCanceledException) { }
        catch { _retryAfter = DateTime.UtcNow.AddSeconds(5); }
        finally { _reading = false; }
    }
    private static bool ShouldTranslate(string text, string language)
    {
        var chinese = language.Contains("中") || language.Contains("Chinese", StringComparison.OrdinalIgnoreCase);
        if (chinese && text.Count(c => c is >= '\u4E00' and <= '\u9FFF') > text.Count(char.IsLetter) / 2) return false;
        // Ambiguous Latin languages are sent to the model; it returns target-language text unchanged.
        return !string.IsNullOrWhiteSpace(text);
    }
    private void PlaceCached(IReadOnlyList<WindowParagraphReader.Paragraph> paragraphs)
    {
        if (_showOriginal || SelectionReader.ForegroundWindow() != _window) return;
        foreach (var paragraph in paragraphs)
        {
            var id = Identity(paragraph);
            if (!_cache.TryGet(CacheKey(paragraph.Text), out var text) || text.Trim() == paragraph.Text.Trim()) continue;
            if (_overlays.TryGetValue(id, out var overlay)) { overlay.MoveToBounds(paragraph.Bounds); if (!overlay.IsVisible) overlay.Show(); }
            else { overlay = new LayerOverlayWindow(text, paragraph.Bounds); _overlays[id] = overlay; overlay.Show(); }
            if (!overlay.Fits) { _model.RetainLayerResult(paragraph.Text, text, "译文超出原段落范围，完整结果已保留。"); Dispose(); return; }
        }
    }
    private async Task TranslateAsync(IReadOnlyList<string> texts, long revision)
    {
        _translating = true;
        var settings = _model.Settings; var target = settings.RequestLanguages().My;
        var action = settings.EffectiveActions.First(a => a.Id == ProcessingAction.TranslationId);
        try
        {
            var request = _target != null
                ? new ProcessingRequest { Text = texts[0], Mode = ProcessingMode.Translate, ActionPrompt = action.Prompt, MyLanguage = target, ForeignLanguage = settings.RequestLanguages().Foreign }
                : new ProcessingRequest { Text = LayerTranslationRequest.BuildUserMessage(texts), Mode = ProcessingMode.Translate, ActionPrompt = action.Prompt, MyLanguage = target, ForeignLanguage = target, LayerTargetLanguage = target };
            var result = new System.Text.StringBuilder();
            await new ModelServiceClient().SendAsync(ModelServiceClient.Prepare(request, settings), settings.ModelService.Format,
                new SecretRedactor(settings.ApiKey), null, piece => result.Append(piece), _cancellation.Token);
            IReadOnlyDictionary<int, string>? parsed = _target != null
                ? string.IsNullOrWhiteSpace(result.ToString()) ? null : new Dictionary<int, string> { [0] = result.ToString().Trim() }
                : LayerTranslationRequest.ParseReply(result.ToString().Trim(), texts.Count);
            if (_disposed) return;
            if (parsed == null) { _retryAfter = DateTime.UtcNow.AddSeconds(10); return; }
            foreach (var (id, text) in parsed) _cache.Set(CacheKey(texts[id]), text);
            if (revision == _revision) PlaceCached(_last);
        }
        catch (OperationCanceledException) { }
        catch (ModelServiceException error)
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
            if (_target != null) { Dispose(); _model.RetainLayerResult(_target.Text, "", "原处翻译失败：" + error.Error.Description()); }
        }
        catch
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
            if (_target != null) { Dispose(); _model.RetainLayerResult(_target.Text, "", "原处翻译未能完成，可在面板中重试。"); }
        }
        finally { _translating = false; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancellation.Cancel(); _clock.Stop(); _watch.Dispose(); _reader.Dispose();
        foreach (var overlay in _overlays.Values) overlay.Close(); _overlays.Clear(); _model.LayerSessionEnded(this);
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
