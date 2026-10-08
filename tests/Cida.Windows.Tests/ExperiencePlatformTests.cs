using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Cida.Core;
using Cida.Platform;
using Xunit;

namespace Cida.Windows.Tests;

public sealed class ExperiencePlatformTests
{
    [Fact]
    public void CorruptSettingsRecoveryPreservesTheGoodBackupAndEncryptedKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "cida-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsFileStore(path); var secrets = new SecretStore(path);
            store.SaveSettings(new CidaSettings { MyLanguage = "日本語" });
            store.SaveSettings(new CidaSettings { MyLanguage = "中文" });
            var backup = File.ReadAllText(Path.Combine(path, "settings.json.bak"));
            File.WriteAllText(Path.Combine(path, "settings.json"), "{broken");
            Assert.Equal("日本語", store.LoadSettings().MyLanguage);
            Assert.NotNull(SettingsFileStore.RecoveryNote);
            store.SaveSettings(new CidaSettings { MyLanguage = "English" });
            Assert.Equal(backup, File.ReadAllText(Path.Combine(path, "settings.json.bak")));
            Assert.Single(Directory.GetFiles(path, "*.damaged-*"));
            secrets.Save("synthetic-test-key");
            Assert.Equal("synthetic-test-key", secrets.Read());
            Assert.DoesNotContain("synthetic-test-key", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(path, "api-key.bin"))));
            secrets.Clear(); Assert.Null(secrets.Read());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact]
    public async Task OfflineOcrRecognizesEnglishAndChineseWithoutWindowsOcrIdentity()
    {
        using var bitmap = new Bitmap(1200, 260);
        using var fonts = new System.Drawing.Text.PrivateFontCollection();
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "CidaChineseSerif.ttf"));
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(fonts.Families[0], 42))
        {
            graphics.Clear(Color.White); graphics.DrawString("Hello Windows 123", font, Brushes.Black, 35, 25);
            graphics.DrawString("你好世界", font, Brushes.Black, 35, 125);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        var lines = await new LocalOcr().RecognizeAsync(stream.ToArray(), bitmap.Width, bitmap.Height, forceFallback: true);
        Assert.NotNull(lines);
        var text = string.Join("\n", lines.Select(line => line.Text));
        Assert.Contains("Windows", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("你好世界", text);
        Assert.All(lines, line => Assert.True(line.Bounds.Width > 0 && line.Bounds.Height > 0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalOcr().RecognizeAsync(stream.ToArray(), bitmap.Width, bitmap.Height, cancellationToken: cancellation.Token, forceFallback: true));
    }
    [Fact]
    public void OcrLayoutPreservesBulletsAndSeparatesColumns()
    {
        LocalOcr.RecognizedLine Line(string text, float x, float y) => new([new(text, new RectangleF(x, y, 100, 20))], new(x, y, 100, 20));
        var paragraphs = RecognizedLayout.Paragraphs([Line("first line", 0, 0), Line("wraps naturally", 0, 23), Line("- next item", 0, 46), Line("right column", 300, 46)]);
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("first line wraps naturally", paragraphs[0].Text);
        Assert.Equal("- next item", paragraphs[1].Text);
        Assert.Equal("right column", paragraphs[2].Text);
        var code = RecognizedLayout.Paragraphs([Line("if (ready) {", 0, 0), Line("return value;", 20, 23), Line("}", 0, 46)]);
        Assert.Single(code); Assert.Equal("if (ready) {\n  return value;\n}", code[0].Text);
    }
}
