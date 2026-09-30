using System.Runtime.InteropServices;
using System.Windows.Threading;

using Cida.Core;
using Cida.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cida.Desktop;

/// <summary>
/// One whole-window translation session: a click-through overlay per paragraph, hidden
/// while the user scrolls and re-read/re-aligned 0.2s after scrolling stops. Windows port
/// of the upstream LayerPaneSession: the 20Hz wheel watch, the stillness delay and the
/// "no leftover overlays after stop" rule.
/// </summary>
public sealed class LayerSession : IDisposable
{
    private readonly AppModel _model;
    private readonly WindowParagraphReader _reader = new();
    private readonly LayerTranslationCache _cache = new();
    private readonly Dictionary<string, LayerOverlayWindow> _overlays = new();
    private readonly DispatcherTimer _wheelClock = new() { Interval = TimeSpan.FromMilliseconds(50) }; // 20 Hz
    private readonly DispatcherTimer _stillClock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly nint _window;
    private DateTime _lastWheel = DateTime.MinValue;
    private int _serial;
    private bool _disposed;

    public LayerSession(AppModel model, nint window)
    {
        _model = model;
        _window = window;
        _wheelClock.Tick += OnWheelClock;
        _stillClock.Tick += OnStillClock;
    }

    public async void Start()
    {
        var paragraphs = await Task.Run(() => _reader.ReadVisibleParagraphs(_window));
        Remember(paragraphs);
        InstallWheelWatch();
        await RefreshOverlaysAsync(paragraphs);
    }

    // MARK: wheel watch (20 Hz poll; a hook would need a DLL, a poll needs nothing)

    private void InstallWheelWatch()
    {
        _wheelClock.Start();
    }

    private void OnWheelClock(object? sender, EventArgs arguments)
    {
        if (_disposed) return;
        if (!IsWindowFront())
        {
            Dispose();
            return;
        }
        // Scrolling shows as paragraphs moving between 20 Hz samples. While they move,
        // hide every overlay; once still for 0.2s, re-read and re-place.
        var moving = ParagraphsMoved();
        if (!moving) return;
        _lastWheel = DateTime.UtcNow;
        foreach (var overlay in _overlays.Values)
        {
            if (overlay.IsVisible) overlay.Hide();
        }
        _stillClock.Stop();
        _stillClock.Start();
    }

    private void OnStillClock(object? sender, EventArgs arguments)
    {
        _stillClock.Stop();
        if (_disposed) return;
        // Still for 0.2s (no movement in the last samples): re-read and re-place.
        _ = RefreshAsync(fullRead: true);
    }

    /// <summary>
    /// Whether tracked paragraphs moved since the previous sample: the scroll signal. The
    /// probe re-reads bounds only, so a still window costs a cheap property fetch.
    /// </summary>
    private bool ParagraphsMoved()
    {
        var paragraphs = _reader.ReReadBounds();
        foreach (var paragraph in paragraphs)
        {
            if (_lastBounds.TryGetValue(WindowParagraphReader.Key(paragraph.RuntimeId), out var previous))
            {
                if (Math.Abs(previous.Y - paragraph.Bounds.Y) > 0.5f
                    || Math.Abs(previous.X - paragraph.Bounds.X) > 0.5f)
                {
                    Remember(paragraphs);
                    return true;
                }
            }
            else
            {
                // A paragraph we had not seen at this position: treat as movement.
                Remember(paragraphs);
                return true;
            }
        }
        Remember(paragraphs);
        return false;
    }

    private void Remember(IReadOnlyList<WindowParagraphReader.Paragraph> paragraphs)
    {
        _lastBounds.Clear();
        foreach (var paragraph in paragraphs)
        {
            _lastBounds[WindowParagraphReader.Key(paragraph.RuntimeId)] =
                (paragraph.Bounds.X, paragraph.Bounds.Y);
        }
    }

    private readonly Dictionary<string, (float X, float Y)> _lastBounds = new();

    private bool IsWindowFront() => GetForegroundWindow() == _window;

    // MARK: refresh

    private Task RefreshAsync(bool fullRead) =>
        fullRead
            ? FullRefreshAsync()
            : Task.CompletedTask;

    private async Task FullRefreshAsync()
    {
        if (_disposed) return;
        var serial = ++_serial;
        var paragraphs = await Task.Run(() => _reader.ReadVisibleParagraphs(_window));
        await RefreshOverlaysAsync(paragraphs, serial);
    }

    private async Task RefreshOverlaysAsync(
        IReadOnlyList<WindowParagraphReader.Paragraph>? fresh = null, int? serialHint = null)
    {
        var serial = serialHint ?? ++_serial;
        var paragraphs = fresh ?? await Task.Run(() => _reader.ReReadBounds());
        if (_disposed || serial != _serial) return;

        // Remove overlays whose paragraph left the view.
        var live = paragraphs.Select(paragraph => WindowParagraphReader.Key(paragraph.RuntimeId)).ToHashSet();
        foreach (var (key, overlay) in _oversets())
        {
            if (!live.Contains(key))
            {
                overlay.Close();
                _overlays.Remove(key);
            }
        }

        // Translate only what the cache lacks; the batch carries the JSON array contract.
        var languages = _model.Settings.RequestLanguages();
        var pending = paragraphs
            .Where(paragraph => !_cache.TryGet(paragraph.Text, out _))
            .Select(paragraph => paragraph.Text)
            .Distinct()
            .ToList();
        if (pending.Count > 0)
        {
            await TranslateBatchAsync(pending, languages.My, serial);
        }
        if (_disposed || serial != _serial) return;

        foreach (var paragraph in paragraphs)
        {
            if (!_cache.TryGet(paragraph.Text, out var translation)) continue;
            PlaceOverlay(paragraph, translation);
        }
    }

    private async Task TranslateBatchAsync(IReadOnlyList<string> paragraphs, string targetLanguage, int serial)
    {
        try
        {
            var request = new ProcessingRequest
            {
                Text = LayerTranslationRequest.BuildUserMessage(paragraphs),
                Mode = ProcessingMode.Translate,
                MyLanguage = targetLanguage,
                ForeignLanguage = targetLanguage,
                LayerTargetLanguage = targetLanguage,
            };
            var prepared = ModelServiceClient.Prepare(request, _model.Settings);
            var collected = "";
            await new ModelServiceClient().SendAsync(
                prepared,
                _model.Settings.ModelService.Format,
                new SecretRedactor(_model.Settings.ApiKey),
                null,
                piece => collected += piece);
            var parsed = LayerTranslationRequest.ParseReply(collected.Trim());
            if (parsed == null) return;
            foreach (var (index, translation) in parsed)
            {
                if (index < paragraphs.Count)
                {
                    _cache.Set(paragraphs[index], translation);
                }
            }
        }
        catch (ModelServiceException)
        {
            // A failed batch leaves the paragraphs un-overlaid; the next refresh retries.
        }
    }

    private void PlaceOverlay(WindowParagraphReader.Paragraph paragraph, string translation)
    {
        var key = WindowParagraphReader.Key(paragraph.RuntimeId);
        if (_overlays.TryGetValue(key, out var existing))
        {
            // Re-place: the paragraph moved with the scroll.
            existing.Left = paragraph.Bounds.Left - 2;
            existing.Top = paragraph.Bounds.Top - 2;
            existing.Width = Math.Max(paragraph.Bounds.Width + 4, 200);
            if (!existing.IsVisible) existing.Show();
            return;
        }
        var overlay = new LayerOverlayWindow(translation, paragraph.Bounds);
        _overlays[key] = overlay;
        overlay.Show();
    }

    private IEnumerable<KeyValuePair<string, LayerOverlayWindow>> _oversets() =>
        _overlays.ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wheelClock.Stop();
        _stillClock.Stop();
        foreach (var overlay in _overlays.Values)
        {
            overlay.Close();
        }
        _overlays.Clear();
        _model.LayerSessionEnded(this);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
