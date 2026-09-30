using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cida.Core;

public enum ProcessingMode
{
    Translate,
    Improve,
}

public static class ProcessingModeExtensions
{
    public static string Title(this ProcessingMode mode) => mode switch
    {
        ProcessingMode.Translate => "翻译",
        ProcessingMode.Improve => "改进",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string RawValue(this ProcessingMode mode) => mode switch
    {
        ProcessingMode.Translate => "translate",
        ProcessingMode.Improve => "improve",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static ProcessingMode? FromRawValue(string? text) => text switch
    {
        "translate" => ProcessingMode.Translate,
        "improve" => ProcessingMode.Improve,
        _ => null,
    };
}

public enum OutputLanguage
{
    Chinese,
    English,
}

public static class OutputLanguageExtensions
{
    public static string Title(this OutputLanguage language) => language switch
    {
        OutputLanguage.Chinese => "中文",
        OutputLanguage.English => "English",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };
}

public sealed record ProcessingRequest
{
    public required string Text { get; init; }
    public required ProcessingMode Mode { get; init; }

    /// <summary>The user's two languages as they wrote them; the model decides direction.</summary>
    public required string MyLanguage { get; init; }
    public required string ForeignLanguage { get; init; }

    /// <summary>
    /// Set for the translation layer's request: the text is a JSON array of numbered
    /// paragraphs, each translated into this language only.
    /// </summary>
    public string? LayerTargetLanguage { get; init; }
}

public enum ResultPhase
{
    /// <summary>The request is running; the renderer shows the caret and streamed text.</summary>
    Streaming,

    Completed,
    Stopped,
    Failed,

    /// <summary>A capture held no text, so nothing was requested.</summary>
    Unrecognized,
}

public sealed record ResultNote(ResultNoteKind Kind, string Text)
{
    public static readonly ResultNote Stale = new(ResultNoteKind.Stale, "原文已修改 · ⏎ 重新生成");
}

public enum ResultNoteKind
{
    Stale,
    Stopped,
    Failed,
    Unrecognized,
}

/// <summary>
/// The settings Cida keeps, serialized as versioned JSON by the platform store. Ported from
/// upstream Models.swift CidaSettings.
/// </summary>
public sealed record CidaSettings
{
    public const string DefaultTranslationPrompt =
        "Translate the user-provided text into the target language specified by the application. Preserve meaning, tone, and terminology. Return only the translated text.";

    public const string DefaultImprovementPrompt =
        "You are a writing assistant. Improve the user-provided text for clarity, grammar, and natural tone. Keep the original language and meaning. Prefer precise technical wording. Return only the improved text.";

    private const int CurrentPromptContractVersion = 2;

    /// <summary>Where requests go and how they are shaped; written by the command line.</summary>
    public ModelConfiguration ModelService { get; init; } = ModelConfiguration.Default;

    /// <summary>The key from the secret store. It is never written with the rest of the settings.</summary>
    public string ApiKey { get; init; } = "";

    public string TranslationPrompt { get; init; } = DefaultTranslationPrompt;
    public string ImprovementPrompt { get; init; } = DefaultImprovementPrompt;

    /// <summary>What every other language is translated into; any wording, e.g. 粤语 or 英式英语.</summary>
    public string MyLanguage { get; init; } = DefaultLanguages().My;

    /// <summary>What text in <see cref="MyLanguage"/> is translated into.</summary>
    public string ForeignLanguage { get; init; } = DefaultLanguages().Foreign;

    public bool LaunchAtLogin { get; init; }

    /// <summary>The combination that shows the panel from any application; null clears it.</summary>
    public GlobalShortcut? Shortcut { get; init; } = GlobalShortcut.AltA;

    /// <summary>The combination that captures text on screen and translates it.</summary>
    public GlobalShortcut? CaptureShortcut { get; init; } = GlobalShortcut.AltS;

    /// <summary>The combination that translates the paragraph under the pointer.</summary>
    public GlobalShortcut? LayerShortcut { get; init; } = GlobalShortcut.AltD;

    public bool IsModelServiceComplete => ModelService.IsComplete(hasApiKey: ApiKey.Length > 0);

    public string ModelServiceFingerprint => ModelService.Fingerprint(ApiKey);

    public GlobalShortcut? ShortcutFor(GlobalShortcutAction action) => action switch
    {
        GlobalShortcutAction.ShowPanel => Shortcut,
        GlobalShortcutAction.CaptureText => CaptureShortcut,
        GlobalShortcutAction.TranslationLayer => LayerShortcut,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Every combination the global shortcuts hold; the layer's also holds its Shift variant.</summary>
    public IReadOnlyList<GlobalShortcut> HeldShortcuts()
    {
        return new[] { Shortcut, CaptureShortcut, LayerShortcut, LayerShortcut?.AddingShift() }
            .Where(shortcut => shortcut != null)
            .Select(shortcut => shortcut!.Value)
            .ToList();
    }

    /// <summary>No two shortcuts share a combination, and the layer's leaves Shift to its variant.</summary>
    public bool HasValidShortcuts =>
        LayerShortcut?.Modifiers.HasFlag(ShortcutModifiers.Shift) != true
        && HeldShortcuts().Distinct().Count() == HeldShortcuts().Count;

    public CidaSettings WithShortcut(GlobalShortcut? newShortcut, GlobalShortcutAction action) => action switch
    {
        GlobalShortcutAction.ShowPanel => this with { Shortcut = newShortcut },
        GlobalShortcutAction.CaptureText => this with { CaptureShortcut = newShortcut },
        GlobalShortcutAction.TranslationLayer => this with { LayerShortcut = newShortcut },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public string PromptFor(ProcessingMode mode) =>
        mode == ProcessingMode.Translate ? TranslationPrompt : ImprovementPrompt;

    public static string DefaultPromptFor(ProcessingMode mode) =>
        mode == ProcessingMode.Translate ? DefaultTranslationPrompt : DefaultImprovementPrompt;

    /// <summary>
    /// The two languages before the user writes any: Cida speaks Chinese, so its user's own
    /// language is Chinese whatever the system language is; 繁體中文 when the system prefers
    /// traditional Chinese.
    /// </summary>
    public static (string My, string Foreign) DefaultLanguages(
        IReadOnlyList<string>? preferredLanguages = null)
    {
        preferredLanguages ??= CultureInfoInstalledTongues();
        var traditional = preferredLanguages.Any(identifier =>
        {
            var dash = identifier.IndexOf('-');
            var language = dash > 0 ? identifier[..dash] : identifier;
            if (language != "zh") return false;
            var region = dash > 0 ? identifier[(dash + 1)..].Split('-')[0] : "";
            return region.Length == 0
                ? false // "zh" alone: simplified by convention on Windows
                : region is "TW" or "HK" or "MO" or "Hant";
        });
        return (traditional ? "繁體中文" : "简体中文", "English");
    }

    private static IReadOnlyList<string> CultureInfoInstalledTongues()
    {
        try
        {
            return System.Globalization.CultureInfo.InstalledUICulture.Name.Length > 0
                ? [System.Globalization.CultureInfo.InstalledUICulture.Name]
                : ["zh-CN"];
        }
        catch
        {
            return ["zh-CN"];
        }
    }

    /// <summary>The languages a request carries: an emptied field means its default.</summary>
    public (string My, string Foreign) RequestLanguages()
    {
        var defaults = DefaultLanguages();
        var my = MyLanguage.Trim();
        var foreign = ForeignLanguage.Trim();
        return (my.Length == 0 ? defaults.My : my, foreign.Length == 0 ? defaults.Foreign : foreign);
    }

    // MARK: Codable

    private class SettingsContract
    {
        [JsonPropertyName("modelService")] public string? ModelServiceText { get; set; }
        [JsonPropertyName("translationPrompt")] public string? TranslationPrompt { get; set; }
        [JsonPropertyName("improvementPrompt")] public string? ImprovementPrompt { get; set; }
        [JsonPropertyName("myLanguage")] public string? MyLanguage { get; set; }
        [JsonPropertyName("foreignLanguage")] public string? ForeignLanguage { get; set; }
        [JsonPropertyName("launchAtLogin")] public bool? LaunchAtLogin { get; set; }
        [JsonPropertyName("shortcut")] public GlobalShortcutContract? Shortcut { get; set; }
        [JsonPropertyName("captureShortcut")] public GlobalShortcutContract? CaptureShortcut { get; set; }
        [JsonPropertyName("layerShortcut")] public GlobalShortcutContract? LayerShortcut { get; set; }
        [JsonPropertyName("promptContractVersion")] public int? PromptContractVersion { get; set; }

        // 1.0's provider preset, model and custom endpoint; read once to build modelService.
        [JsonPropertyName("provider")] public string? LegacyProvider { get; set; }
        [JsonPropertyName("model")] public string? LegacyModel { get; set; }
        [JsonPropertyName("openAIEndpoint")] public string? LegacyEndpoint { get; set; }

        // Tracks whether the shortcut keys were present (missing = default, null = cleared).
        [JsonIgnore] public bool ShortcutPresent { get; set; }
        [JsonIgnore] public bool CaptureShortcutPresent { get; set; }
        [JsonIgnore] public bool LayerShortcutPresent { get; set; }
    }

    private sealed class GlobalShortcutContract
    {
        [JsonPropertyName("keyCode")] public ushort KeyCode { get; set; }
        [JsonPropertyName("modifiers")] public byte Modifiers { get; set; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CidaSettings FromJsonText(string text)
    {
        SettingsContract? contract;
        string? serviceText = null;
        try
        {
            using var probe = JsonDocument.Parse(text);
            if (probe.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new CidaSettings();
            }
            serviceText = probe.RootElement.TryGetProperty("modelService", out var serviceElement)
                && serviceElement.ValueKind == JsonValueKind.Object
                ? serviceElement.GetRawText()
                : null;
            // Strip the modelService member (an object) before deserializing: the contract
            // holds it as ordered JSON text, which System.Text.Json would refuse to map.
            var remainder = new Dictionary<string, JsonElement>();
            foreach (var member in probe.RootElement.EnumerateObject())
            {
                if (member.Name != "modelService")
                {
                    remainder[member.Name] = member.Value;
                }
            }
            var remainderText = remainder.Count > 0 ? JsonSerializer.Serialize(remainder) : "{}";
            contract = JsonSerializer.Deserialize<SettingsContract>(remainderText, SerializerOptions)
                ?? new SettingsContract();
            contract.ShortcutPresent = remainder.ContainsKey("shortcut");
            contract.CaptureShortcutPresent = remainder.ContainsKey("captureShortcut");
            contract.LayerShortcutPresent = remainder.ContainsKey("layerShortcut");
            contract.ModelServiceText = serviceText;
        }
        catch (JsonException)
        {
            return new CidaSettings();
        }
        if (contract == null) return new CidaSettings();

        var modelService = serviceText != null
            ? ModelConfiguration.FromJson(JsonValue.Parse(serviceText)?.ObjectValue)
            : contract.LegacyProvider != null || contract.LegacyModel != null || contract.LegacyEndpoint != null
                ? ModelConfiguration.Migrating(
                    contract.LegacyProvider, contract.LegacyModel, contract.LegacyEndpoint)
                : ModelConfiguration.Default;

        var promptContractVersion = contract.PromptContractVersion ?? 1;
        var translationPrompt = contract.TranslationPrompt ?? DefaultTranslationPrompt;
        var improvementPrompt = contract.ImprovementPrompt ?? DefaultImprovementPrompt;
        if (promptContractVersion < CurrentPromptContractVersion)
        {
            translationPrompt = MigratingLegacyPrompt(translationPrompt);
            improvementPrompt = MigratingLegacyPrompt(improvementPrompt);
        }

        var defaults = DefaultLanguages();
        GlobalShortcut? ReadShortcut(GlobalShortcutContract? value, bool present, GlobalShortcut fallback) =>
            !present ? fallback : value == null ? null : new GlobalShortcut(value.KeyCode, (ShortcutModifiers)value.Modifiers);

        return new CidaSettings
        {
            ModelService = modelService,
            TranslationPrompt = translationPrompt,
            ImprovementPrompt = improvementPrompt,
            MyLanguage = contract.MyLanguage ?? defaults.My,
            ForeignLanguage = contract.ForeignLanguage ?? defaults.Foreign,
            LaunchAtLogin = contract.LaunchAtLogin ?? false,
            Shortcut = ReadShortcut(contract.Shortcut, contract.ShortcutPresent, GlobalShortcut.AltA),
            CaptureShortcut = ReadShortcut(contract.CaptureShortcut, contract.CaptureShortcutPresent, GlobalShortcut.AltS),
            LayerShortcut = ReadShortcut(contract.LayerShortcut, contract.LayerShortcutPresent, GlobalShortcut.AltD),
        };
    }

    private static string MigratingLegacyPrompt(string prompt) =>
        prompt.Replace("{text}", "the user-provided text")
            .Replace("{target_lang}", "the target language specified in the trusted runtime parameters");

    public string ToJsonText()
    {
        var serviceText = JsonValue.Object(ModelService.ToJson()).CompactText;
        var contract = new SettingsContract
        {
            ModelServiceText = serviceText,
            TranslationPrompt = TranslationPrompt,
            ImprovementPrompt = ImprovementPrompt,
            MyLanguage = MyLanguage,
            ForeignLanguage = ForeignLanguage,
            LaunchAtLogin = LaunchAtLogin,
            Shortcut = WriteShortcut(Shortcut),
            CaptureShortcut = WriteShortcut(CaptureShortcut),
            LayerShortcut = WriteShortcut(LayerShortcut),
            PromptContractVersion = CurrentPromptContractVersion,
        };
        // Serialize with the modelService member as a placeholder string, then splice the
        // ordered JSON in: System.Text.Json would otherwise escape it as opaque text.
        var text = JsonSerializer.Serialize(contract, SerializerOptions);
        var marker = JsonSerializer.Serialize(serviceText, SerializerOptions);
        return text.Replace(marker, serviceText);
    }

    private static GlobalShortcutContract? WriteShortcut(GlobalShortcut? shortcut) =>
        shortcut == null ? null : new GlobalShortcutContract
        {
            KeyCode = shortcut.Value.KeyCode,
            Modifiers = (byte)shortcut.Value.Modifiers,
        };
}

/// <summary>The outcome of the latest check, kept with the settings.</summary>
public sealed record ModelServiceCheckRecord(
    bool Passed,
    int? StatusCode,
    string? Reason,
    DateTime CheckedAt,
    string Fingerprint)
{
    /// <summary><c>401 · 服务商拒绝了 API Key</c>, or the reason alone when no response arrived.</summary>
    public string? FailureSummary()
    {
        if (Passed) return null;
        var reason = Reason ?? "检查失败";
        return StatusCode is int status ? $"{status} · {reason}" : reason;
    }
}

public static class ModelServiceCheckRecordExtensions
{
    private class RecordContract
    {
        [JsonPropertyName("passed")] public bool Passed { get; set; }
        [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("checkedAt")] public string? CheckedAt { get; set; }
        [JsonPropertyName("fingerprint")] public string? Fingerprint { get; set; }
    }

    public static string ToJsonText(this ModelServiceCheckRecord record)
    {
        var contract = new RecordContract
        {
            Passed = record.Passed,
            StatusCode = record.StatusCode,
            Reason = record.Reason,
            CheckedAt = record.CheckedAt.ToString("O"),
            Fingerprint = record.Fingerprint,
        };
        return JsonSerializer.Serialize(contract);
    }

    public static ModelServiceCheckRecord? FromJsonText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var contract = JsonSerializer.Deserialize<RecordContract>(text);
            if (contract?.Fingerprint == null) return null;
            return new ModelServiceCheckRecord(
                contract.Passed,
                contract.StatusCode,
                contract.Reason,
                DateTime.TryParse(contract.CheckedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    ? at
                    : DateTime.Now,
                contract.Fingerprint);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
