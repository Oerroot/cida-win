using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Cida.Core;

namespace Cida.Platform;

/// <summary>
/// Screen capture through GDI (BitBlt), the simple all-versions path for a frozen-screen
/// flow: one capture per monitor at native resolution, no cursor.
/// </summary>
public sealed class ScreenCapturer
{
    public sealed record CapturedScreen(byte[] Pixels, int Width, int Height, Rectangle Bounds);

    public IReadOnlyList<CapturedScreen> CaptureAll()
    {
        var screens = new List<CapturedScreen>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            screens.Add(Capture(screen.Bounds));
        }
        return screens;
    }

    public CapturedScreen Capture(Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        return new CapturedScreen(memory.ToArray(), bounds.Width, bounds.Height, bounds);
    }
}

/// <summary>
/// Local OCR through Windows.Media.Ocr, when the process has package identity (a sparse
/// package or MSIX install) and the language pack is present. Ported shape from upstream
/// TextRecognition.swift; the line-merging geometry lives in <see cref="RecognizedLayout"/>.
/// </summary>
public sealed class LocalOcr
{
    public sealed record RecognizedWord(string Text, RectangleF Bounds);
    public sealed record RecognizedLine(IReadOnlyList<RecognizedWord> Words, RectangleF Bounds)
    {
        public string Text => JoinWords(Words.Select(word => word.Text));
    }

    public bool IsAvailable(string languageTag = "zh-Hans")
    {
        return TryCreateEngine(languageTag, out _) || FallbackAvailable;
    }

    /// <summary>The OCR engine available for the language, or the first Chinese one.</summary>
    public static IReadOnlyList<string> AvailableLanguageTags()
    {
        try
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryCreateEngine(string languageTag, out Windows.Media.Ocr.OcrEngine? engine)
    {
        try
        {
            var language = new Windows.Globalization.Language(languageTag);
            engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(language);
            return engine != null;
        }
        catch
        {
            engine = null;
            return false;
        }
    }

    /// <summary>Recognizes text in a captured image; null when OCR is unavailable.</summary>
    private async Task<IReadOnlyList<RecognizedLine>?> RecognizeWindowsAsync(
        byte[] pngPixels, int width, int height, string languageTag = "zh-Hans")
    {
        if (!TryCreateEngine(languageTag, out var engine) || engine == null)
        {
            return null;
        }
        using var softwareBitmap = await SoftwareBitmapFromPngAsync(pngPixels);
        if (softwareBitmap == null) return null;
        var result = await engine.RecognizeAsync(softwareBitmap);
        var lines = new List<RecognizedLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word => new RecognizedWord(
                word.Text,
                new RectangleF(
                    (float)word.BoundingRect.X,
                    (float)word.BoundingRect.Y,
                    (float)word.BoundingRect.Width,
                    (float)word.BoundingRect.Height))).ToList();
            if (words.Count == 0) continue;
            var left = words.Min(word => word.Bounds.Left);
            var top = words.Min(word => word.Bounds.Top);
            var right = words.Max(word => word.Bounds.Right);
            var bottom = words.Max(word => word.Bounds.Bottom);
            lines.Add(new RecognizedLine(words, RectangleF.FromLTRB(left, top, right, bottom)));
        }
        return lines;
    }

    private static async Task<Windows.Graphics.Imaging.SoftwareBitmap?> SoftwareBitmapFromPngAsync(
        byte[] png)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
        }
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
    }
    private static string DataPath => Path.Combine(AppContext.BaseDirectory, "tessdata");
    public static bool FallbackAvailable => new[] { "eng", "chi_sim", "chi_tra" }.All(language => File.Exists(Path.Combine(DataPath, language + ".traineddata")));
    public static string CapabilitySummary()
    {
        var tags = AvailableLanguageTags();
        var native = tags.Count > 0 ? "系统 OCR：" + string.Join("、", tags) : "系统 OCR：当前不可用";
        if (!FallbackAvailable) return native + "\n离线 OCR：语言资源缺失，请重新下载安装包。";
        try
        {
            using var engine = new Tesseract.TesseractEngine(DataPath, "chi_sim+eng", Tesseract.EngineMode.LstmOnly);
            return native + "\n离线 OCR：简体中文、繁体中文、英文已就绪，无需网络或身份包。";
        }
        catch { return native + "\n离线 OCR：引擎无法加载，请重新安装完整版本。"; }
    }
    public async Task<IReadOnlyList<RecognizedLine>?> RecognizeAsync(byte[] pngPixels, int width, int height,
        string languageTag = "zh-Hans", CancellationToken cancellationToken = default, bool forceFallback = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceFallback && TryCreateEngine(languageTag, out _))
        {
            try
            {
                var maximum = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
                if (width <= maximum && height <= maximum)
                {
                    var native = await RecognizeWindowsAsync(pngPixels, width, height, languageTag);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (native is { Count: > 0 }) return native;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Package identity/language limitations never disable the offline fallback. */ }
        }
        if (!FallbackAvailable) return null;
        return await Task.Run(() =>
        {
            var language = languageTag.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "chi_tra+eng" : "chi_sim+eng";
            using var engine = new Tesseract.TesseractEngine(DataPath, language, Tesseract.EngineMode.LstmOnly);
            using var image = Tesseract.Pix.LoadFromMemory(pngPixels);
            using var page = engine.Process(image, Tesseract.PageSegMode.Auto);
            using var iterator = page.GetIterator();
            var lines = new List<RecognizedLine>();
            iterator.Begin();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = iterator.GetText(Tesseract.PageIteratorLevel.TextLine)?.Trim();
                if (!string.IsNullOrEmpty(text) && iterator.TryGetBoundingBox(Tesseract.PageIteratorLevel.TextLine, out var rect))
                {
                    var bounds = new RectangleF(rect.X1, rect.Y1, rect.Width, rect.Height);
                    lines.Add(new([new RecognizedWord(text, bounds)], bounds));
                }
            } while (iterator.Next(Tesseract.PageIteratorLevel.TextLine));
            return (IReadOnlyList<RecognizedLine>)lines;
        }, cancellationToken);
    }
    internal static string JoinWords(IEnumerable<string> words)
    {
        var result = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (word.Length == 0) continue;
            if (result.Length > 0 && !(IsHan(result[^1]) && IsHan(word[0]))) result.Append(' ');
            result.Append(word);
        }
        return result.ToString();
    }
    internal static bool IsHan(char value) => value is >= '\u3400' and <= '\u9FFF';
}

/// <summary>
/// The pure geometry that turns recognized lines into paragraphs: line merging and reading
/// order, ported from upstream RecognizedTextLayout (fully decoupled from the OCR engine).
/// </summary>
public static class RecognizedLayout
{
    public sealed record Paragraph(IReadOnlyList<LocalOcr.RecognizedLine> Lines, RectangleF Bounds)
    {
        public string Text => MergeLines(Lines);
    }

    public static IReadOnlyList<Paragraph> Paragraphs(IReadOnlyList<LocalOcr.RecognizedLine> lines)
    {
        var ordered = lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToList();
        var paragraphs = new List<Paragraph>();
        LocalOcr.RecognizedLine? previous = null;
        var current = new List<LocalOcr.RecognizedLine>();
        foreach (var line in ordered)
        {
            if (previous != null && !BelongsToSameParagraph(previous, line))
            {
                paragraphs.Add(MakeParagraph(current));
                current = [];
            }
            current.Add(line);
            previous = line;
        }
        if (current.Count > 0) paragraphs.Add(MakeParagraph(current));
        return paragraphs;
    }

    /// <summary>
    /// Two lines join a paragraph when they overlap horizontally and the vertical gap is
    /// under 0.8 of the taller line's height (the upstream paragraph-spacing rule).
    /// </summary>
    private static bool BelongsToSameParagraph(LocalOcr.RecognizedLine above, LocalOcr.RecognizedLine below)
    {
        var gap = below.Bounds.Top - (above.Bounds.Top + above.Bounds.Height);
        var taller = Math.Max(above.Bounds.Height, below.Bounds.Height);
        if (taller <= 0) return false;
        if (gap < -taller * 0.3 || gap > taller * 0.8) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(below.Text, @"^(?:[-*•]|\d+[.)])\s")) return false;
        var overlap = Math.Min(
            above.Bounds.Right, below.Bounds.Right) - Math.Max(above.Bounds.Left, below.Bounds.Left);
        return overlap > 0;
    }

    private static Paragraph MakeParagraph(IReadOnlyList<LocalOcr.RecognizedLine> lines)
    {
        var left = lines.Min(line => line.Bounds.Left);
        var top = lines.Min(line => line.Bounds.Top);
        var right = lines.Max(line => line.Bounds.Right);
        var bottom = lines.Max(line => line.Bounds.Bottom);
        return new Paragraph(lines, RectangleF.FromLTRB(left, top, right, bottom));
    }
    private static string MergeLines(IReadOnlyList<LocalOcr.RecognizedLine> lines)
    {
        var code = lines.Count > 1 && (lines.Sum(line => line.Text.Count(ch => ch is '{' or '}' or ';')) >= 2 ||
            lines.Any(line => System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"^(?:def |class |const |let |function |public |return |if\s*\(|for\s*\()")));
        if (code)
        {
            var left = lines.Min(line => line.Bounds.Left);
            return string.Join("\n", lines.Select(line => new string(' ', Math.Clamp((int)Math.Round((line.Bounds.Left - left) / Math.Max(1, line.Bounds.Height) * 2), 0, 16)) + line.Text));
        }
        var result = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var text = line.Text;
            if (result.Length > 0 && text.Length > 0)
            {
                if (result[^1] == '-' && char.IsLetter(text[0])) result.Length--;
                else if (!(LocalOcr.IsHan(result[^1]) && LocalOcr.IsHan(text[0]))) result.Append(' ');
            }
            result.Append(text);
        }
        return result.ToString();
    }
}
