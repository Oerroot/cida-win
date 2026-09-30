using System.Security.Cryptography;
using System.Text;

namespace Cida.Core;

/// <summary>
/// How a request and its streamed reply are shaped. Ported from upstream ModelConfiguration.swift.
/// </summary>
public enum ModelRequestFormat
{
    /// <summary>OpenAI Chat Completions and the many services compatible with it.</summary>
    ChatCompletions,

    /// <summary>OpenAI Responses.</summary>
    Responses,

    /// <summary>Anthropic Messages.</summary>
    AnthropicMessages,
}

public static class ModelRequestFormatExtensions
{
    public static string RawValue(this ModelRequestFormat format) => format switch
    {
        ModelRequestFormat.ChatCompletions => "chat-completions",
        ModelRequestFormat.Responses => "responses",
        ModelRequestFormat.AnthropicMessages => "anthropic-messages",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static ModelRequestFormat? FromRawValue(string? text) => text switch
    {
        "chat-completions" => ModelRequestFormat.ChatCompletions,
        "responses" => ModelRequestFormat.Responses,
        "anthropic-messages" => ModelRequestFormat.AnthropicMessages,
        _ => null,
    };

    public static IReadOnlyList<ModelRequestFormat> AllCases { get; } =
        [ModelRequestFormat.ChatCompletions, ModelRequestFormat.Responses, ModelRequestFormat.AnthropicMessages];

    /// <summary>The name Settings shows beside the host.</summary>
    public static string DisplayName(this ModelRequestFormat format) => format switch
    {
        ModelRequestFormat.ChatCompletions => "Chat Completions",
        ModelRequestFormat.Responses => "Responses",
        ModelRequestFormat.AnthropicMessages => "Anthropic Messages",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static ModelAuthentication DefaultAuthentication(this ModelRequestFormat format) =>
        format == ModelRequestFormat.AnthropicMessages ? ModelAuthentication.XApiKey : ModelAuthentication.Bearer;
}

/// <summary>Which request header carries the API key.</summary>
public enum ModelAuthentication
{
    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    Bearer,

    /// <summary><c>x-api-key: &lt;key&gt;</c>, as Anthropic expects.</summary>
    XApiKey,

    /// <summary><c>api-key: &lt;key&gt;</c>, as Azure OpenAI expects.</summary>
    ApiKey,

    /// <summary>No key is sent.</summary>
    None,
}

public static class ModelAuthenticationExtensions
{
    public static string RawValue(this ModelAuthentication auth) => auth switch
    {
        ModelAuthentication.Bearer => "bearer",
        ModelAuthentication.XApiKey => "x-api-key",
        ModelAuthentication.ApiKey => "api-key",
        ModelAuthentication.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(auth)),
    };

    public static IReadOnlyList<ModelAuthentication> AllCases { get; } =
    [
        ModelAuthentication.Bearer, ModelAuthentication.XApiKey, ModelAuthentication.ApiKey,
        ModelAuthentication.None,
    ];

    public static ModelAuthentication? FromRawValue(string? text) => text switch
    {
        "bearer" => ModelAuthentication.Bearer,
        "x-api-key" => ModelAuthentication.XApiKey,
        "api-key" => ModelAuthentication.ApiKey,
        "none" => ModelAuthentication.None,
        _ => null,
    };

    public static string? HeaderName(this ModelAuthentication auth) => auth switch
    {
        ModelAuthentication.Bearer => "Authorization",
        ModelAuthentication.XApiKey => "x-api-key",
        ModelAuthentication.ApiKey => "api-key",
        ModelAuthentication.None => null,
        _ => throw new ArgumentOutOfRangeException(nameof(auth)),
    };

    public static string HeaderValue(this ModelAuthentication auth, string apiKey) =>
        auth == ModelAuthentication.Bearer ? $"Bearer {apiKey}" : apiKey;
}

/// <summary>
/// The model service every request goes to. The command line writes it and Settings only
/// shows it. The API key is not part of it: it lives in the platform secret store (DPAPI on
/// Windows) and travels beside it in <see cref="CidaSettings.ApiKey"/>.
/// </summary>
public sealed record ModelConfiguration
{
    public string Endpoint { get; init; } = "";
    public ModelRequestFormat Format { get; init; } = ModelRequestFormat.ChatCompletions;

    /// <summary>The model name; empty until configured.</summary>
    public string Model { get; init; } = "";

    /// <summary>The key's header; null follows <see cref="Format"/>.</summary>
    public ModelAuthentication? Auth { get; init; }

    /// <summary>Extra request headers, sent after Cida's own so they can replace them.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Extra body members, merged into the body Cida builds.</summary>
    public JsonObject Body { get; init; } = new();

    public static readonly ModelConfiguration Default = new();

    // Dictionary member equality is reference-based; compare the headers by content.
    public bool Equals(ModelConfiguration? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Endpoint != other.Endpoint || Format != other.Format || Model != other.Model
            || Auth != other.Auth || !Body.Equals(other.Body))
        {
            return false;
        }
        if (Headers.Count != other.Headers.Count) return false;
        foreach (var (key, value) in Headers)
        {
            if (!other.Headers.TryGetValue(key, out var otherValue) || value != otherValue)
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Endpoint, Format, Model, Auth, Body);

    public ModelAuthentication ResolvedAuth => Auth ?? Format.DefaultAuthentication();

    /// <summary>The endpoint when it is an http(s) URL with a host.</summary>
    public Uri? EndpointUrl => EndpointUrlFrom(Endpoint);

    public static Uri? EndpointUrlFrom(string text)
    {
        var value = text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(url.Host)) return null;
        return url;
    }

    public string? Host => EndpointUrl?.Host;

    /// <summary>A server on this machine, which may run without a key.</summary>
    public bool IsLocalEndpoint
    {
        get
        {
            var host = Host?.ToLowerInvariant();
            if (host == null) return false;
            var bare = host.Trim('[', ']');
            return bare is "localhost" or "127.0.0.1" or "::1";
        }
    }

    public bool RequiresApiKey => ResolvedAuth != ModelAuthentication.None && !IsLocalEndpoint;

    public bool IsUnset => this == Default;

    /// <summary>The fields a request still needs, in the command line's names.</summary>
    public IReadOnlyList<string> MissingFields(bool hasApiKey)
    {
        var missing = new List<string>();
        if (EndpointUrl == null) missing.Add("endpoint");
        if (string.IsNullOrWhiteSpace(Model)) missing.Add("model");
        if (RequiresApiKey && !hasApiKey) missing.Add("api-key");
        return missing;
    }

    /// <summary>Whether requests can be sent.</summary>
    public bool IsComplete(bool hasApiKey) => MissingFields(hasApiKey).Count == 0;

    /// <summary><c>api.deepseek.com</c>: where requests go, as Settings shows it.</summary>
    public string HostTitle => Host ?? "未设置端点";

    /// <summary>The model with its reasoning level, where requests go and in which shape.</summary>
    public string Summary
    {
        get
        {
            var modelName = Model.Trim();
            var modelTitle = string.Join(" ",
                new[] { modelName.Length == 0 ? "未设置模型" : modelName, Reasoning }
                    .Where(part => part != null).Select(part => part!));
            return $"{modelTitle} · {HostTitle} · {Format.DisplayName()}";
        }
    }

    /// <summary>
    /// The reasoning level <see cref="Body"/> asks for, spelled as the service spells it, or
    /// null when reasoning is off or left to the service's default.
    /// </summary>
    public string? Reasoning
    {
        get
        {
            var efforts = new[]
            {
                Body["reasoning"]?["effort"], Body["reasoning_effort"], Body["output_config"]?["effort"],
            };
            var effort = efforts.FirstOrDefault(value =>
                value?.StringValue is { Length: > 0 });
            if (effort?.StringValue is { } text)
            {
                return text == "none" ? null : text;
            }
            var thinkingType = Body["thinking"]?["type"]?.StringValue;
            if (thinkingType != null)
            {
                if (thinkingType == "disabled") return null;
                var budget = Body["thinking"]?["budget_tokens"]?.IntValue;
                return budget is int tokens ? $"thinking {tokens}" : "thinking";
            }
            if (Body["enable_thinking"] is { TokenType: JsonValue.Kind.Bool, Value: true })
            {
                return "thinking";
            }
            return null;
        }
    }

    /// <summary>
    /// Identifies what a check tested: the service, the request shape and the key. Only a
    /// digest leaves this function, so the key cannot be read back from it.
    /// </summary>
    public string Fingerprint(string apiKey)
    {
        var headerObject = new JsonObject();
        foreach (var name in Headers.Keys.Order(StringComparer.Ordinal))
        {
            headerObject[name] = JsonValue.String(Headers[name]);
        }
        var canonical = new JsonObject
        {
            ["endpoint"] = JsonValue.String(Endpoint),
            ["format"] = JsonValue.String(Format.RawValue()),
            ["model"] = JsonValue.String(Model),
            ["auth"] = JsonValue.String(ResolvedAuth.RawValue()),
            ["headers"] = JsonValue.Object(headerObject),
            ["body"] = JsonValue.Object(Body.SortedByKey()),
        };
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonValue.Object(canonical).CompactText)
                .Concat([(byte)0])
                .Concat(Encoding.UTF8.GetBytes(apiKey))
                .ToArray());
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    // MARK: Migration

    /// <summary>The configuration a 1.0 provider preset stood for.</summary>
    public static ModelConfiguration Migrating(string? provider, string? model, string? endpoint)
    {
        var presetEndpoints = new Dictionary<string, string>
        {
            ["DeepSeek"] = "https://api.deepseek.com/chat/completions",
            ["OpenAI"] = "https://api.openai.com/v1/chat/completions",
            ["Moonshot"] = "https://api.moonshot.cn/v1/chat/completions",
            ["Zhipu"] = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
        };
        var providerName = provider ?? "DeepSeek";
        var customEndpoint = endpoint ?? "";
        var configuration = Default with { Model = model ?? "deepseek-chat" };
        if (providerName == "Custom"
            || (providerName == "OpenAI" && customEndpoint.Length > 0
                && customEndpoint != presetEndpoints.GetValueOrDefault("OpenAI")))
        {
            return configuration with { Endpoint = customEndpoint };
        }
        return configuration with
        {
            Endpoint = presetEndpoints.GetValueOrDefault(providerName)
                ?? presetEndpoints["DeepSeek"],
        };
    }

    // MARK: Codable (upstream keeps key order stable; we serialize to JSON text)

    public static ModelConfiguration FromJson(JsonObject? objectValue)
    {
        if (objectValue == null) return Default;
        return new ModelConfiguration
        {
            Endpoint = objectValue["endpoint"]?.StringValue ?? "",
            Format = ModelRequestFormatExtensions.FromRawValue(objectValue["format"]?.StringValue)
                ?? ModelRequestFormat.ChatCompletions,
            Model = objectValue["model"]?.StringValue ?? "",
            Auth = ModelAuthenticationExtensions.FromRawValue(objectValue["auth"]?.StringValue),
            Headers = ReadHeaders(objectValue["headers"]),
            Body = objectValue["body"]?.ObjectValue ?? new JsonObject(),
        };
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonValue? value)
    {
        var headers = new Dictionary<string, string>();
        if (value?.ObjectValue is { } headerObject)
        {
            foreach (var member in headerObject.Members)
            {
                if (member.Value.StringValue is { } text)
                {
                    headers[member.Key] = text;
                }
            }
        }
        return headers;
    }

    public JsonObject ToJson()
    {
        var headers = new JsonObject();
        foreach (var name in Headers.Keys.Order(StringComparer.Ordinal))
        {
            headers[name] = JsonValue.String(Headers[name]);
        }
        var result = new JsonObject
        {
            ["endpoint"] = JsonValue.String(Endpoint),
            ["format"] = JsonValue.String(Format.RawValue()),
            ["model"] = JsonValue.String(Model),
            ["auth"] = Auth == null ? JsonValue.Null : JsonValue.String(Auth.Value.RawValue()),
            ["headers"] = JsonValue.Object(headers),
            ["body"] = JsonValue.Object(Body),
        };
        return result;
    }
}
