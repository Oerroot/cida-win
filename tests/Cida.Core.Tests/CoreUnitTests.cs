using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class ModelsTests
{
    [Fact]
    public void SettingsRoundTripKeepsValues()
    {
        var settings = new CidaSettings
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.deepseek.com/chat/completions",
                Model = "deepseek-chat",
                Headers = new Dictionary<string, string> { ["X-Extra"] = "1" },
                Body = new JsonObject
                {
                    ["thinking"] = JsonValue.Object(new JsonObject
                    {
                        ["type"] = JsonValue.String("disabled"),
                    }),
                },
            },
            ApiKey = "",
            MyLanguage = "粤语",
            ForeignLanguage = "英式英语",
            LaunchAtLogin = true,
        };
        var restored = CidaSettings.FromJsonText(settings.ToJsonText());
        Assert.Equal(settings, restored);
    }

    [Fact]
    public void LegacyProviderPresetMigrates()
    {
        var legacy = """
            {"provider":"Moonshot","model":"kimi-k2","myLanguage":"简体中文","foreignLanguage":"English"}
            """;
        var settings = CidaSettings.FromJsonText(legacy);
        Assert.Equal("https://api.moonshot.cn/v1/chat/completions", settings.ModelService.Endpoint);
        Assert.Equal("kimi-k2", settings.ModelService.Model);
    }

    [Fact]
    public void LegacyPromptPlaceholdersMigrate()
    {
        var legacy = """
            {"translationPrompt":"Translate {text} into {target_lang}.","promptContractVersion":1}
            """;
        var settings = CidaSettings.FromJsonText(legacy);
        Assert.DoesNotContain("{text}", settings.TranslationPrompt);
        Assert.Contains("the user-provided text", settings.TranslationPrompt);
        Assert.Contains("trusted runtime parameters", settings.TranslationPrompt);
    }

    [Fact]
    public void MissingShortcutKeysUseDefaultsNullClears()
    {
        var defaults = CidaSettings.FromJsonText("{}");
        Assert.Equal(GlobalShortcut.AltA, defaults.Shortcut);

        var cleared = CidaSettings.FromJsonText("""{"shortcut":null}""");
        Assert.Null(cleared.Shortcut);
        Assert.Equal(GlobalShortcut.AltS, cleared.CaptureShortcut);
    }

    [Fact]
    public void FingerprintChangesWithApiKey()
    {
        var configuration = new ModelConfiguration
        {
            Endpoint = "https://api.example.com/v1",
            Model = "m",
        };
        Assert.NotEqual(configuration.Fingerprint("key-one"), configuration.Fingerprint("key-two"));
        Assert.Equal(configuration.Fingerprint("key-one"), configuration.Fingerprint("key-one"));
    }

    [Fact]
    public void HeldShortcutsValidation()
    {
        Assert.True(new CidaSettings().HasValidShortcuts);
        var conflicting = new CidaSettings() with { CaptureShortcut = GlobalShortcut.AltA };
        Assert.False(conflicting.HasValidShortcuts);
        var shifted = new CidaSettings() with
        {
            Shortcut = new GlobalShortcut(0x44, ShortcutModifiers.Alt | ShortcutModifiers.Shift),
        };
        Assert.False(shifted.HasValidShortcuts);
    }
}

public sealed class JsonValueTests
{
    [Fact]
    public void CompactTextWritesMembersInInsertionOrder()
    {
        var value = JsonValue.Object(new JsonObject
        {
            ["model"] = JsonValue.String("m"),
            ["stream"] = JsonValue.Bool(true),
            ["messages"] = JsonValue.Array([JsonValue.String("a")]),
        });
        Assert.Equal("{\"model\":\"m\",\"stream\":true,\"messages\":[\"a\"]}", value.CompactText);
    }

    [Fact]
    public void DisplayTextAddsSpaces()
    {
        var value = JsonValue.Object(new JsonObject { ["a"] = JsonValue.Integer(1) });
        Assert.Equal("{\"a\": 1}", value.DisplayText);
    }

    [Fact]
    public void ParsedObjectsSortKeys()
    {
        var value = JsonValue.Parse("{\"b\":1,\"a\":2}");
        Assert.Equal("{\"a\":2,\"b\":1}", value!.CompactText);
    }

    [Fact]
    public void MergingOverridesNestedObjectsAndNullRemoves()
    {
        var @base = JsonValue.Object(new JsonObject
        {
            ["model"] = JsonValue.String("m"),
            ["nested"] = JsonValue.Object(new JsonObject
            {
                ["a"] = JsonValue.Integer(1),
                ["b"] = JsonValue.Integer(2),
            }),
        });
        var merged = @base.Merging(JsonValue.Parse(
            """{"nested":{"b":3},"stream":true,"model":null}""")!);
        Assert.Null(merged["model"]);
        Assert.Equal(1, merged["nested"]!["a"]!.IntValue);
        Assert.Equal(3, merged["nested"]!["b"]!.IntValue);
        Assert.True(merged["stream"]!.StringValue == null && merged["stream"]!.TokenType == JsonValue.Kind.Bool);
    }

    [Fact]
    public void EscapesControlCharactersAndQuotes()
    {
        var value = JsonValue.String("a\"b\nc");
        Assert.Equal("\"a\\\"b\\nc\"", value.CompactText);
    }

    [Fact]
    public void NumberKindsSurviveParse()
    {
        var value = JsonValue.Parse("{\"i\":42,\"f\":1.5,\"big\":9007199254740993}")!;
        Assert.Equal(JsonValue.Kind.Integer, value["i"]!.TokenType);
        Assert.Equal(JsonValue.Kind.Number, value["f"]!.TokenType);
        Assert.Equal(JsonValue.Kind.Integer, value["big"]!.TokenType);
    }
}

public sealed class GlobalShortcutTests
{
    [Fact]
    public void ConfigurationTextRoundTrips()
    {
        var shortcut = GlobalShortcut.FromConfigurationText("control+option+t");
        Assert.NotNull(shortcut);
        Assert.Equal("control+option+t", shortcut!.Value.ConfigurationText);
    }

    [Fact]
    public void PlainKeyWithoutModifiersIsRejected()
    {
        Assert.Null(GlobalShortcut.FromConfigurationText("a"));
    }

    [Fact]
    public void AliasesResolve()
    {
        Assert.Equal(GlobalShortcut.FromConfigurationText("alt+a"), GlobalShortcut.FromConfigurationText("option+a"));
        Assert.Equal(GlobalShortcut.FromConfigurationText("win+a"), GlobalShortcut.FromConfigurationText("command+a"));
        Assert.Equal(GlobalShortcut.FromConfigurationText("enter"), GlobalShortcut.FromConfigurationText("return"));
    }

    [Fact]
    public void AddingShiftKeepsKey()
    {
        var shifted = GlobalShortcut.AltD.AddingShift();
        Assert.Equal(GlobalShortcut.AltD.KeyCode, shifted.KeyCode);
        Assert.True(shifted.Modifiers.HasFlag(ShortcutModifiers.Shift));
    }
}

public sealed class SecretRedactorTests
{
    [Fact]
    public void RedactsPlainTextJsonEscapedAndPercentEncoded()
    {
        var redactor = new SecretRedactor("sk/abcd1234");
        Assert.Equal("key ••••", redactor.Redact("key sk/abcd1234"));
        Assert.Equal("x •••• y", redactor.Redact("x sk\\/abcd1234 y"));
    }

    [Fact]
    public void ShortSecretsAreLeftAlone()
    {
        var redactor = new SecretRedactor("abc");
        Assert.Equal("abc", redactor.Redact("abc"));
    }
}

public sealed class PromptTests
{
    [Fact]
    public void TranslatePromptCarriesParametersAndLanguages()
    {
        var request = new ProcessingRequest
        {
            Text = "hello",
            Mode = ProcessingMode.Translate,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
        };
        var prompt = ModelPromptBuilder.Build(request, new CidaSettings());
        Assert.Contains("translate_between", prompt.SystemMessage);
        Assert.Contains("简体中文", prompt.SystemMessage);
        Assert.Contains("hello", prompt.UserMessage);
        Assert.Contains("Application contract:", prompt.SystemMessage);
    }

    [Fact]
    public void ImprovePromptPreservesSource()
    {
        var request = new ProcessingRequest
        {
            Text = "文本",
            Mode = ProcessingMode.Improve,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
        };
        var prompt = ModelPromptBuilder.Build(request, new CidaSettings());
        Assert.Contains("preserve_source", prompt.SystemMessage);
    }

    [Fact]
    public void LayerRequestUsesTranslateIntoContract()
    {
        var request = new ProcessingRequest
        {
            Text = "[{\"id\":1,\"text\":\"a\"}]",
            Mode = ProcessingMode.Translate,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
            LayerTargetLanguage = "简体中文",
        };
        var prompt = ModelPromptBuilder.Build(request, new CidaSettings());
        Assert.Contains("translate_into", prompt.SystemMessage);
        Assert.Contains("JSON array of paragraphs", prompt.SystemMessage);
        Assert.Contains("target_language", prompt.SystemMessage);
    }

    [Fact]
    public void CustomPromptReplacesDefault()
    {
        var request = new ProcessingRequest
        {
            Text = "x",
            Mode = ProcessingMode.Translate,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
        };
        var settings = new CidaSettings { TranslationPrompt = "自定义策略" };
        var prompt = ModelPromptBuilder.Build(request, settings);
        Assert.StartsWith("自定义策略", prompt.SystemMessage);
    }
}

public sealed class LanguageDetectorTests
{
    [Theory]
    [InlineData("这是中文", OutputLanguage.Chinese)]
    [InlineData("English text", OutputLanguage.English)]
    [InlineData("12345", null)]
    public void DetectByScript(string text, OutputLanguage? expected)
    {
        Assert.Equal(expected, TextLanguageDetector.Detect(text));
    }
}
