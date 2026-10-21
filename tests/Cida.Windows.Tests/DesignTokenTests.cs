using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cida.Desktop;
using Xunit;

namespace Cida.Windows.Tests;

/// <summary>
/// #80 text hierarchy: essential instructions, feedback and quiet commands use secondary text,
/// quieter labels use tertiary, and the decorative hint token never carries text the user must
/// read to act. These checks are headless: the palette is a pure accessor and the XAML assertions
/// only read the source file, so no WPF Application or desktop session is needed.
/// </summary>
public sealed class DesignTokenTests
{
    /// <summary>WCAG 2.1 relative luminance of an sRGB hex color.</summary>
    private static double Luminance(string hex)
    {
        static double Channel(int value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        var color = hex.TrimStart('#');
        return 0.2126 * Channel(Convert.ToInt32(color[..2], 16))
            + 0.7152 * Channel(Convert.ToInt32(color.Substring(2, 2), 16))
            + 0.0722 * Channel(Convert.ToInt32(color.Substring(4, 2), 16));
    }

    private static double Contrast(string first, string second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Dictionary<string, string> Palette(bool dark) =>
        ThemeService.Palette(dark).ToDictionary(entry => entry.Name, entry => entry.Hex);

    [Fact]
    public void PaletteKeepsTheExistingThemeNamesAndAddsTheTextLevels()
    {
        string[] expected =
        [
            "PanelBackground", "Paper", "PanelBorder", "Ink", "InkSecondary", "InkTertiary", "InkHint",
            "Accent", "AccentSoft", "Error", "AccentInk", "ControlBackground", "ControlBorder",
            "ControlHover", "ControlPressed", "AccentPressed", "DisabledInk",
        ];
        foreach (var dark in new[] { false, true })
        {
            var palette = ThemeService.Palette(dark);
            Assert.Equal(expected.Length, palette.Count);
            Assert.Equal(expected, palette.Select(entry => entry.Name).ToArray());
            Assert.All(palette, entry => Assert.Matches("^#[0-9A-F]{6}$", entry.Hex));
            var values = palette.ToDictionary(entry => entry.Name, entry => entry.Hex);
            Assert.NotEqual(values["InkTertiary"], values["InkHint"]);
            Assert.NotEqual(values["InkSecondary"], values["InkTertiary"]);
        }
    }

    [Fact]
    public void SecondaryAndTertiaryTextMeetContrastOnPanelAndPaperInBothThemes()
    {
        foreach (var dark in new[] { false, true })
        {
            var palette = Palette(dark);
            foreach (var surface in new[] { "PanelBackground", "Paper" })
            {
                var secondary = Contrast(palette["InkSecondary"], palette[surface]);
                var tertiary = Contrast(palette["InkTertiary"], palette[surface]);
                Assert.True(secondary >= 4.5,
                    $"{SurfaceName(dark)} InkSecondary on {surface} is {secondary:0.00}:1, below the 4.5:1 text floor.");
                Assert.True(tertiary >= 3.0,
                    $"{SurfaceName(dark)} InkTertiary on {surface} is {tertiary:0.00}:1, below the 3.0:1 quiet-text floor.");
                // Primary text must stay clearly above secondary so the hierarchy does not flatten.
                Assert.True(Contrast(palette["Ink"], palette[surface]) > secondary);
                Assert.True(secondary > tertiary);
            }
        }
    }

    [Fact]
    public void LightSecondaryIsNotLightenedBelowTheOldInkToken()
    {
        var palette = Palette(dark: false);
        foreach (var surface in new[] { "PanelBackground", "Paper" })
        {
            var current = Contrast(palette["InkSecondary"], palette[surface]);
            var previous = Contrast("#646C60", palette[surface]);
            Assert.True(current >= previous - 1e-9,
                $"Light InkSecondary contrast on {surface} dropped from {previous:0.00} to {current:0.00}.");
        }
        var dark = Palette(dark: true);
        Assert.True(Contrast(dark["InkSecondary"], dark["PanelBackground"]) >= 4.5);
        Assert.True(Contrast(dark["InkSecondary"], dark["Paper"]) >= 4.5);
    }

    [Fact]
    public void SettingsXamlKeepsEssentialTextOnSecondaryAndHintsDecorative()
    {
        var settings = FindDesktopFile("SettingsWindow.xaml");
        var controls = FindDesktopFile(Path.Combine("Resources", "Controls.xaml"));
        if (settings == null || controls == null) return; // A packaged test payload cannot read the checkout; palette checks above still ran.
        var xaml = File.ReadAllText(settings);
        Assert.Contains("{DynamicResource InkSecondary}", xaml);
        // The recorder/run key hint is the one decorative use, and it is distinct from tertiary.
        Assert.Contains("Foreground=\"{DynamicResource InkHint}\"", xaml);
        var hintLine = LineContaining(xaml, "x:Name=\"PreviewKeyHint\"");
        Assert.NotNull(hintLine);
        Assert.Contains("InkHint", hintLine!);
        // Essential instructions, feedback and quiet commands must never drop to the hint token.
        string[] essential =
        [
            "PreviewStatus", "PreviewFailure", "ActionEditHint", "PreviewReferenceLabel", "ActionPromptTitle",
            "ShortcutStatus", "ShortcutCapabilities", "PanelKeys",
            "UpdateStatus", "UpdateNotes", "UpdateHint", "UpdateDeferredNote",
            "ModelSummary", "CheckStatus", "Capabilities", "SaveStatus", "VersionLabel",
        ];
        foreach (var name in essential)
        {
            var line = LineContaining(xaml, $"x:Name=\"{name}\"");
            Assert.NotNull(line);
            Assert.DoesNotContain("InkHint", line!);
        }
        var controlsXaml = File.ReadAllText(controls);
        Assert.Contains("x:Key=\"InkTertiary\"", controlsXaml);
        Assert.Contains("x:Key=\"InkHint\"", controlsXaml);
        // The static design-time dictionary and the runtime palette must agree on the new levels.
        foreach (var (name, hex) in ThemeService.Palette(dark: false))
        {
            if (name is not ("InkTertiary" or "InkHint")) continue;
            Assert.Contains($"x:Key=\"{name}\" Color=\"{hex}\"", controlsXaml);
        }
    }

    private static string SurfaceName(bool dark) => dark ? "dark" : "light";

    private static string? LineContaining(string text, string needle)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(needle, StringComparison.Ordinal)) return line;
        }
        return null;
    }

    /// <summary>Locates a Cida.Desktop source file from the test payload by walking up the tree.</summary>
    private static string? FindDesktopFile(string relative)
    {
        var roots = new List<string> { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; depth < 10 && directory != null; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "windows", "Cida.Desktop", relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
