using System.Text;

namespace Cida.Core;

/// <summary>
/// Everything the command line can read and write, as one value: the settings (without the
/// key) and the login item kept outside them. Ported from upstream ConfigurationFields.swift.
/// </summary>
public sealed record EditableConfiguration
{
    public required CidaSettings Settings { get; init; }
    public bool LaunchAtLogin { get; init; }

    public static readonly EditableConfiguration Default = new()
    {
        Settings = new CidaSettings(),
        LaunchAtLogin = false,
    };
}

public sealed record InvalidValue(string Field, string Message);

/// <summary>
/// A setting by its command-line name. The schema, parsing, validation and display of each
/// field live here, so <c>config schema</c>, <c>set</c>, <c>unset</c> and <c>show</c> cannot
/// disagree about a field.
/// </summary>
public enum ConfigurationField
{
    Endpoint,
    Format,
    Model,
    ApiKey,

    Auth,
    Headers,
    Body,
    MyLanguage,
    ForeignLanguage,
    TranslationPrompt,
    ImprovementPrompt,
    Shortcut,
    CaptureShortcut,
    LayerShortcut,
    ImproveShortcut,
    LaunchAtLogin,
}

public static class ConfigurationFieldExtensions
{
    public static readonly IReadOnlyList<ConfigurationField> AllCases =
        Enum.GetValues<ConfigurationField>();

    /// <summary>The fields that describe the model service; <c>show</c> always lists these.</summary>
    public static readonly IReadOnlyList<ConfigurationField> ModelServiceFields =
    [
        ConfigurationField.Endpoint, ConfigurationField.Format, ConfigurationField.Model,
        ConfigurationField.ApiKey, ConfigurationField.Auth, ConfigurationField.Headers,
        ConfigurationField.Body,
    ];

    public static string RawValue(this ConfigurationField field) => field switch
    {
        ConfigurationField.Endpoint => "endpoint",
        ConfigurationField.Format => "format",
        ConfigurationField.Model => "model",
        ConfigurationField.ApiKey => "api-key",
        ConfigurationField.Auth => "auth",
        ConfigurationField.Headers => "headers",
        ConfigurationField.Body => "body",
        ConfigurationField.MyLanguage => "my-language",
        ConfigurationField.ForeignLanguage => "foreign-language",
        ConfigurationField.TranslationPrompt => "translation-prompt",
        ConfigurationField.ImprovementPrompt => "improvement-prompt",
        ConfigurationField.Shortcut => "shortcut",
        ConfigurationField.CaptureShortcut => "capture-shortcut",
        ConfigurationField.LayerShortcut => "layer-shortcut",
        ConfigurationField.ImproveShortcut => "improve-shortcut",
        ConfigurationField.LaunchAtLogin => "launch-at-login",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static ConfigurationField? FromRawValue(string? text) =>
        AllCases.FirstOrDefault(field => field.RawValue() == text) is var match && match != default
            ? match
            : text == null ? null : AllCases.FirstOrDefault(field => field.RawValue() == text) == default ? null : AllCases.First(field => field.RawValue() == text);

    /// <summary>Written only from <c>--stdin</c>, <c>--file</c> or <c>--env</c>, and never printed.</summary>
    public static bool IsSecret(this ConfigurationField field) => field == ConfigurationField.ApiKey;

    // MARK: Schema

    public sealed record Schema(
        string Type,
        IReadOnlyList<string>? Values,
        string DefaultValue,
        string Example,
        string Description);

    public static Schema GetSchema(this ConfigurationField field) => field switch
    {
        ConfigurationField.Endpoint => new Schema(
            "url", null, "未设置", "https://api.deepseek.com/chat/completions",
            "请求发往的完整地址（http 或 https，包含路径）"),
        ConfigurationField.Format => new Schema(
            "enum", ModelRequestFormatExtensions.AllCases.Select(f => f.RawValue()).ToList(),
            ModelRequestFormat.ChatCompletions.RawValue(),
            ModelRequestFormat.AnthropicMessages.RawValue(),
            "请求与流式响应的格式：chat-completions 为 OpenAI Chat Completions 及兼容它的服务，responses 为 OpenAI Responses，anthropic-messages 为 Anthropic Messages"),
        ConfigurationField.Model => new Schema(
            "string", null, "未设置", "deepseek-chat", "模型名，原样写进请求体的 model"),
        ConfigurationField.ApiKey => new Schema(
            "secret", null, "未设置", "Cida config set api-key --stdin < 密钥.txt",
            "API Key，存进系统密钥库（DPAPI）；只能用 --stdin、--file 或 --env 写入，不接受写在命令里的值，也不会被输出；端点在本机（localhost、127.0.0.1、::1）或 auth 为 none 时可不设"),
        ConfigurationField.Auth => new Schema(
            "enum", ModelAuthenticationExtensions.AllCases.Select(a => a.RawValue()).ToList(),
            "随 format：anthropic-messages 为 x-api-key，其余为 bearer",
            ModelAuthentication.ApiKey.RawValue(),
            "API Key 放在哪个请求头：bearer 为 Authorization: Bearer <key>，x-api-key 与 api-key 为同名请求头，none 不发送 Key"),
        ConfigurationField.Headers => new Schema(
            "json-object", null, "{}", "{\"HTTP-Referer\": \"https://example.com\"}",
            "额外请求头，值都是字符串；与辞达自己的请求头同名时以这里为准"),
        ConfigurationField.Body => new Schema(
            "json-object", null, "{}", "{\"thinking\": {\"type\": \"disabled\"}}",
            "合并进请求体的额外参数（如关闭推理、改 max_tokens）；对象逐层合并，值为 null 的键会从请求里去掉"),
        ConfigurationField.MyLanguage => new Schema(
            "text", null, CidaSettings.DefaultLanguages().My, "粤语",
            "我的语言，与设置里的相同：其他语言都译成它；可写任何语言、方言、地区写法或文体，由模型理解"),
        ConfigurationField.ForeignLanguage => new Schema(
            "text", null, CidaSettings.DefaultLanguages().Foreign, "英式英语",
            "常用外语，与设置里的相同：原文是我的语言时译成它；写法同 my-language"),
        ConfigurationField.TranslationPrompt => new Schema(
            "text", null, CidaSettings.DefaultTranslationPrompt, "Cida config set translation-prompt --file prompt.txt",
            "翻译的提示词，与设置里的相同；目标语言与任务由辞达传入，不必写占位符；长文本可用 --file 或 --stdin"),
        ConfigurationField.ImprovementPrompt => new Schema(
            "text", null, CidaSettings.DefaultImprovementPrompt, "Cida config set improvement-prompt --file prompt.txt",
            "改进的提示词，与设置里的相同；长文本可用 --file 或 --stdin"),
        ConfigurationField.Shortcut => new Schema(
            "shortcut", null, GlobalShortcut.AltA.ConfigurationText, "control+alt+t",
            "显示辞达的全局快捷键，与设置里的相同：修饰键（control、option/alt、shift、command/win）加一个键，用 + 连接，至少带 control、option 或 command 之一；none 表示不设置"),
        ConfigurationField.CaptureShortcut => new Schema(
            "shortcut", null, GlobalShortcut.AltS.ConfigurationText, "control+alt+s",
            "截图翻译的快捷键，写法同 shortcut（none 表示不设置），三个快捷键不能相同"),
        ConfigurationField.LayerShortcut => new Schema(
            "shortcut", null, GlobalShortcut.AltD.ConfigurationText, "control+alt+d",
            "原处翻译的快捷键：把指针下这一段换成译文、再按换回；加 shift 翻译整个窗口。写法同 shortcut（none 表示不设置），不能带 shift，三个快捷键（含加 shift 的这个）不能相同"),
        ConfigurationField.ImproveShortcut => new Schema("shortcut", null, GlobalShortcut.AltF.ConfigurationText,
            "control+alt+f", "改进并安全替换选区；再次按下取消。none 表示关闭。"),
        ConfigurationField.LaunchAtLogin => new Schema(
            "boolean", ["true", "false"], "false", "true",
            "开机启动，与设置里的开关相同"),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    // MARK: Writing

    /// <summary>
    /// Parses <paramref name="text"/> and writes it into a new configuration. The key is not
    /// handled here: it goes straight to the secret store.
    /// </summary>
    public static EditableConfiguration Apply(
        this ConfigurationField field, string text, EditableConfiguration configuration)
    {
        var value = text.Trim();
        var settings = configuration.Settings;
        switch (field)
        {
            case ConfigurationField.Endpoint:
                if (value.Length == 0) throw new InvalidConfiguration(field, "不能为空；要清除用 config unset endpoint");
                if (ModelConfiguration.EndpointUrlFrom(value) == null)
                {
                    throw new InvalidConfiguration(field, $"不是有效的 http 或 https 地址：{value}");
                }
                settings = settings with { ModelService = settings.ModelService with { Endpoint = value } };
                break;
            case ConfigurationField.Format:
            {
                var format = ModelRequestFormatExtensions.FromRawValue(value);
                if (format == null)
                {
                    throw new InvalidConfiguration(field,
                        $"只能是 {List(ModelRequestFormatExtensions.AllCases.Select(f => f.RawValue()).ToList())}");
                }
                settings = settings with { ModelService = settings.ModelService with { Format = format.Value } };
                break;
            }
            case ConfigurationField.Model:
                if (value.Length == 0) throw new InvalidConfiguration(field, "不能为空；要清除用 config unset model");
                settings = settings with { ModelService = settings.ModelService with { Model = value } };
                break;
            case ConfigurationField.ApiKey:
                break;
            case ConfigurationField.Auth:
            {
                var auth = ModelAuthenticationExtensions.FromRawValue(value);
                if (auth == null)
                {
                    throw new InvalidConfiguration(field,
                        $"只能是 {List(ModelAuthenticationExtensions.AllCases.Select(a => a.RawValue()).ToList())}");
                }
                settings = settings with { ModelService = settings.ModelService with { Auth = auth } };
                break;
            }
            case ConfigurationField.Headers:
                settings = settings with { ModelService = settings.ModelService with { Headers = ParseHeaders(field, value) } };
                break;
            case ConfigurationField.Body:
            {
                if (JsonValue.Parse(value)?.ObjectValue is not { } body)
                {
                    throw new InvalidConfiguration(field,
                        "要是一个 JSON 对象，例如 {\"thinking\": {\"type\": \"disabled\"}}");
                }
                settings = settings with { ModelService = settings.ModelService with { Body = body } };
                break;
            }
            case ConfigurationField.MyLanguage or ConfigurationField.ForeignLanguage:
            {
                if (value.Length == 0 || value.Contains('\n'))
                {
                    throw new InvalidConfiguration(field,
                        $"要是一行文字，例如 {field.GetSchema().Example}；要恢复默认用 config unset {field.RawValue()}");
                }
                settings = field == ConfigurationField.MyLanguage
                    ? settings with { MyLanguage = value }
                    : settings with { ForeignLanguage = value };
                break;
            }
            case ConfigurationField.TranslationPrompt or ConfigurationField.ImprovementPrompt:
            {
                if (value.Length == 0)
                {
                    throw new InvalidConfiguration(field, $"不能为空；要恢复默认用 config unset {field.RawValue()}");
                }
                settings = field == ConfigurationField.TranslationPrompt
                    ? settings with { TranslationPrompt = value }
                    : settings with { ImprovementPrompt = value };
                break;
            }
            case ConfigurationField.Shortcut or ConfigurationField.CaptureShortcut
                or ConfigurationField.LayerShortcut or ConfigurationField.ImproveShortcut:
            {
                var action = field.ShortcutAction()!.Value;
                if (value.ToLowerInvariant() == GlobalShortcut.NoneConfigurationText)
                {
                    settings = settings.WithShortcut(null, action);
                }
                else if (GlobalShortcut.FromConfigurationText(value) is { } shortcut)
                {
                    settings = settings.WithShortcut(shortcut, action);
                }
                else
                {
                    throw new InvalidConfiguration(field,
                        $"写成修饰键加一个键，至少带 control、option 或 command，例如 {field.GetSchema().Example}；不设置写 none");
                }
                break;
            }
            case ConfigurationField.LaunchAtLogin:
            {
                var enabled = value switch
                {
                    "true" => true,
                    "false" => false,
                    _ => (bool?)null,
                };
                if (enabled == null)
                {
                    throw new InvalidConfiguration(field, "只能是 true 或 false");
                }
                configuration = configuration with { LaunchAtLogin = enabled.Value };
                settings = settings with { LaunchAtLogin = enabled.Value };
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
        return configuration with { Settings = settings };
    }

    /// <summary>Puts the field back to its default.</summary>
    public static EditableConfiguration Reset(
        this ConfigurationField field, EditableConfiguration configuration)
    {
        var defaults = new CidaSettings();
        var settings = configuration.Settings;
        switch (field)
        {
            case ConfigurationField.Endpoint:
                settings = settings with { ModelService = settings.ModelService with { Endpoint = "" } };
                break;
            case ConfigurationField.Format:
                settings = settings with { ModelService = settings.ModelService with { Format = ModelRequestFormat.ChatCompletions } };
                break;
            case ConfigurationField.Model:
                settings = settings with { ModelService = settings.ModelService with { Model = "" } };
                break;
            case ConfigurationField.ApiKey:
                break;
            case ConfigurationField.Auth:
                settings = settings with { ModelService = settings.ModelService with { Auth = null } };
                break;
            case ConfigurationField.Headers:
                settings = settings with { ModelService = settings.ModelService with { Headers = new Dictionary<string, string>() } };
                break;
            case ConfigurationField.Body:
                settings = settings with { ModelService = settings.ModelService with { Body = new JsonObject() } };
                break;
            case ConfigurationField.MyLanguage:
                settings = settings with { MyLanguage = defaults.MyLanguage };
                break;
            case ConfigurationField.ForeignLanguage:
                settings = settings with { ForeignLanguage = defaults.ForeignLanguage };
                break;
            case ConfigurationField.TranslationPrompt:
                settings = settings with { TranslationPrompt = defaults.TranslationPrompt };
                break;
            case ConfigurationField.ImprovementPrompt:
                settings = settings with { ImprovementPrompt = defaults.ImprovementPrompt };
                break;
            case ConfigurationField.Shortcut:
                settings = settings.WithShortcut(defaults.Shortcut, GlobalShortcutAction.ShowPanel);
                break;
            case ConfigurationField.CaptureShortcut:
                settings = settings.WithShortcut(defaults.CaptureShortcut, GlobalShortcutAction.CaptureText);
                break;
            case ConfigurationField.LayerShortcut:
                settings = settings.WithShortcut(defaults.LayerShortcut, GlobalShortcutAction.TranslationLayer);
                break;
            case ConfigurationField.ImproveShortcut:
                settings = settings.WithShortcut(defaults.ImproveShortcut, GlobalShortcutAction.ImproveAndReplace);
                break;
            case ConfigurationField.LaunchAtLogin:
                configuration = configuration with { LaunchAtLogin = false };
                settings = settings with { LaunchAtLogin = false };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
        return configuration with { Settings = settings };
    }

    /// <summary>Rules that span fields; checked after every change in a command is applied.</summary>
    public static void Validate(EditableConfiguration configuration)
    {
        var settings = configuration.Settings;
        if (settings.LayerShortcut?.Modifiers.HasFlag(ShortcutModifiers.Shift) == true)
        {
            throw new InvalidConfiguration(ConfigurationField.LayerShortcut,
                "layer-shortcut 不能带 shift：加 shift 是翻译整个窗口");
        }
        foreach (var field in new[] { ConfigurationField.Shortcut, ConfigurationField.CaptureShortcut, ConfigurationField.ImproveShortcut })
        {
            if (settings.LayerShortcut != null
                && settings.ShortcutFor(field.ShortcutAction()!.Value)
                    == settings.LayerShortcut.Value.AddingShift())
            {
                throw new InvalidConfiguration(field,
                    $"{field.RawValue()} 不能与 layer-shortcut 加 shift 相同（翻译整个窗口）");
            }
        }
        var fields = new[] { ConfigurationField.Shortcut, ConfigurationField.CaptureShortcut, ConfigurationField.LayerShortcut, ConfigurationField.ImproveShortcut };
        for (var index = 0; index < fields.Length; index++)
        {
            foreach (var earlier in fields[..index])
            {
                var current = settings.ShortcutFor(fields[index].ShortcutAction()!.Value);
                var previous = settings.ShortcutFor(earlier.ShortcutAction()!.Value);
                if (current != null && current == previous)
                {
                    throw new InvalidConfiguration(fields[index],
                        $"{fields[index].RawValue()} 不能与 {earlier.RawValue()} 相同");
                }
            }
        }
    }

    /// <summary>The global shortcut a shortcut field sets.</summary>
    public static GlobalShortcutAction? ShortcutAction(this ConfigurationField field) => field switch
    {
        ConfigurationField.Shortcut => GlobalShortcutAction.ShowPanel,
        ConfigurationField.CaptureShortcut => GlobalShortcutAction.CaptureText,
        ConfigurationField.LayerShortcut => GlobalShortcutAction.TranslationLayer,
        ConfigurationField.ImproveShortcut => GlobalShortcutAction.ImproveAndReplace,
        _ => null,
    };

    private static IReadOnlyDictionary<string, string> ParseHeaders(
        ConfigurationField field, string value)
    {
        if (JsonValue.Parse(value)?.ObjectValue is not { } headerObject)
        {
            throw new InvalidConfiguration(field,
                "要是一个 JSON 对象，例如 {\"HTTP-Referer\": \"https://example.com\"}");
        }
        var headers = new Dictionary<string, string>();
        const string tokenCharacters = "!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        foreach (var member in headerObject.Members)
        {
            if (member.Key.Length == 0 || member.Key.Any(ch => !tokenCharacters.Contains(ch)))
            {
                throw new InvalidConfiguration(field, $"里的「{member.Key}」不是有效的请求头名");
            }
            var text = member.Value.StringValue;
            if (text == null || text.Contains('\n') || text.Contains('\r'))
            {
                throw new InvalidConfiguration(field, $"里「{member.Key}」的值要是单行字符串");
            }
            headers[member.Key] = text;
        }
        return headers;
    }

    private static string List(IReadOnlyList<string> values)
    {
        if (values.Count <= 1) return values.Count > 0 ? values[0] : "";
        return string.Join("、", values.Take(values.Count - 1)) + " 或 " + values[^1];
    }

    // MARK: Reading

    public static bool IsDefault(
        this ConfigurationField field, EditableConfiguration configuration, bool hasApiKey)
    {
        if (field == ConfigurationField.ApiKey) return !hasApiKey;
        return FieldJson(field, configuration, hasApiKey).Equals(FieldJson(field, EditableConfiguration.Default, hasApiKey));
    }

    /// <summary>The value for <c>show --json</c>. The key appears only as stored or unset.</summary>
    public static JsonValue FieldJson(
        this ConfigurationField field, EditableConfiguration configuration, bool hasApiKey)
    {
        var settings = configuration.Settings;
        var service = settings.ModelService;
        switch (field)
        {
            case ConfigurationField.Endpoint:
                return service.Endpoint.Length == 0 ? Core.JsonValue.Null : Core.JsonValue.String(service.Endpoint);
            case ConfigurationField.Format:
                return Core.JsonValue.String(service.Format.RawValue());
            case ConfigurationField.Model:
                return service.Model.Length == 0 ? Core.JsonValue.Null : Core.JsonValue.String(service.Model);
            case ConfigurationField.ApiKey:
                return Core.JsonValue.String(hasApiKey ? "stored" : "unset");
            case ConfigurationField.Auth:
                return Core.JsonValue.String(service.ResolvedAuth.RawValue());
            case ConfigurationField.Headers:
            {
                var headerObject = new JsonObject();
                foreach (var name in service.Headers.Keys.Order(StringComparer.Ordinal))
                {
                    headerObject[name] = Core.JsonValue.String(service.Headers[name]);
                }
                return Core.JsonValue.Object(headerObject);
            }
            case ConfigurationField.Body:
                return Core.JsonValue.Object(service.Body);
            case ConfigurationField.MyLanguage:
                return Core.JsonValue.String(settings.MyLanguage);
            case ConfigurationField.ForeignLanguage:
                return Core.JsonValue.String(settings.ForeignLanguage);
            case ConfigurationField.TranslationPrompt:
                return Core.JsonValue.String(settings.TranslationPrompt);
            case ConfigurationField.ImprovementPrompt:
                return Core.JsonValue.String(settings.ImprovementPrompt);
            case ConfigurationField.Shortcut or ConfigurationField.CaptureShortcut
                or ConfigurationField.LayerShortcut or ConfigurationField.ImproveShortcut:
                return Core.JsonValue.String(
                    settings.ShortcutFor(field.ShortcutAction()!.Value)?.ConfigurationText
                    ?? GlobalShortcut.NoneConfigurationText);
            case ConfigurationField.LaunchAtLogin:
                return Core.JsonValue.Bool(configuration.LaunchAtLogin);
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    /// <summary>The value for <c>show</c>: one line, the key never, and <c>auth</c> marked when it follows <c>format</c>.</summary>
    public static string DisplayValue(
        this ConfigurationField field, EditableConfiguration configuration, bool hasApiKey)
    {
        var service = configuration.Settings.ModelService;
        switch (field)
        {
            case ConfigurationField.Endpoint or ConfigurationField.Model:
                return field.FieldJson(configuration, hasApiKey).StringValue ?? "未设置";
            case ConfigurationField.ApiKey:
                return hasApiKey ? "已保存在系统密钥库" : "未设置";
            case ConfigurationField.Auth:
                return service.Auth == null
                    ? $"{service.ResolvedAuth.RawValue()} （随 format）"
                    : service.ResolvedAuth.RawValue();
            case ConfigurationField.Headers or ConfigurationField.Body:
                return field.FieldJson(configuration, hasApiKey).DisplayText;
            case ConfigurationField.TranslationPrompt or ConfigurationField.ImprovementPrompt:
            {
                var prompt = field == ConfigurationField.TranslationPrompt
                    ? configuration.Settings.TranslationPrompt
                    : configuration.Settings.ImprovementPrompt;
                var line = prompt.Replace("\n", " ");
                return line.Length > 60 ? line[..60] + "…" : line;
            }
            default:
            {
                var value = field.FieldJson(configuration, hasApiKey);
                return value.StringValue ?? value.CompactText;
            }
        }
    }
}

public sealed class InvalidConfiguration(ConfigurationField field, string message)
    : Exception($"{field.RawValue()} {message}")
{
    public InvalidValue ToInvalidValue() => new(field.RawValue(), $"{field.RawValue()} {Message.Replace($"{field.RawValue()} ", "")}");
}

public static class StringPadding
{
    /// <summary>Pads to <paramref name="width"/> terminal columns; CJK characters take two.</summary>
    public static string Padded(this string text, int width)
    {
        var columns = 0;
        foreach (var scalar in text)
        {
            columns += scalar >= 0x2E80 ? 2 : 1;
        }
        return columns >= width ? text + " " : text + new string(' ', width - columns);
    }
}
