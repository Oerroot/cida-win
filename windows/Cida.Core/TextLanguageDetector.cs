namespace Cida.Core;

/// <summary>
/// The script a text needs: CJK when it holds Han, kana or Hangul, Latin when it holds other
/// letters, null before either shows up. Ported from upstream TextLanguageDetector.swift; the
/// NaturalLanguage recognizer is replaced by script-range analysis (Windows has no
/// NLLanguageRecognizer; the upstream fallback path is the same idea).
/// </summary>
public static class TextLanguageDetector
{
    private const int SampleLimit = 2048;

    public static OutputLanguage? Detect(string text)
    {
        var sample = text.Length <= SampleLimit ? text : text[..SampleLimit];
        if (!sample.Any(ch => !char.IsWhiteSpace(ch))) return null;
        return TypographyOf(sample);
    }

    /// <summary>
    /// The result typography <paramref name="text"/> needs: CJK when it holds Han, kana or
    /// Hangul, Latin when it holds other letters, null before either shows up.
    /// </summary>
    public static OutputLanguage? TypographyOf(string text)
    {
        var sample = text.Length <= SampleLimit ? text : text[..SampleLimit];
        foreach (var ch in sample)
        {
            if (IsKanaOrHangul(ch) || IsCjk(ch)) return OutputLanguage.Chinese;
        }
        foreach (var ch in sample)
        {
            if (char.IsLetter(ch)) return OutputLanguage.English;
        }
        return null;
    }

    private static bool IsKanaOrHangul(char ch) => ch switch
    {
        >= '\u3040' and <= '\u30FF' => true,
        >= '\u1100' and <= '\u11FF' => true,
        >= '\u3130' and <= '\u318F' => true,
        >= '\uAC00' and <= '\uD7AF' => true,
        _ => false,
    };

    private static bool IsCjk(char ch) => ch switch
    {
        >= '\u3400' and <= '\u4DBF' => true,
        >= '\u4E00' and <= '\u9FFF' => true,
        >= '\uF900' and <= '\uFAFF' => true,
        _ => false,
    };
}

public static class ImprovementPresentation
{
    public const string ProfileTitle = "语气与语法";
    public const string FollowsSourceTitle = "输出跟随原文";

    public static string ComposerHint(string text) =>
        TextLanguageDetector.Detect(text) is { } language
            ? $"{language.Title()} · {FollowsSourceTitle}"
            : FollowsSourceTitle;

    public static string HistoryDetail(string source) =>
        $"{(TextLanguageDetector.Detect(source) ?? OutputLanguage.Chinese).Title()} · {ProfileTitle}";
}
