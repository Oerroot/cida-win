using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Cida.Core;

/// <summary>
/// One server-sent event: its <c>event:</c> name, if any, and its <c>data:</c> lines joined
/// by newlines.
/// </summary>
public sealed record ServerSentEvent(string? Name, string Data);

/// <summary>
/// Assembles events from lines (https://html.spec.whatwg.org/multipage/server-sent-events.html):
/// an empty line ends an event, <c>:</c> starts a comment, and several <c>data:</c> lines join.
/// </summary>
public struct ServerSentEventParser
{
    private string? _name;
    private List<string>? _dataLines;

    public ServerSentEvent? Consume(string line)
    {
        if (line.Length == 0) return Dispatch();
        if (line.StartsWith(':')) return null;
        string field;
        string value;
        var colon = line.IndexOf(':');
        if (colon >= 0)
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
        }
        else
        {
            field = line;
            value = "";
        }
        switch (field)
        {
            case "data":
                (_dataLines ??= []).Add(value);
                break;
            case "event":
                _name = value;
                break;
        }
        return null;
    }

    /// <summary>The event still open when the stream ends.</summary>
    public ServerSentEvent? Finish() => Dispatch();

    private ServerSentEvent? Dispatch()
    {
        if (_dataLines == null || _dataLines.Count == 0)
        {
            _name = null;
            return null;
        }
        var @event = new ServerSentEvent(_name, string.Join("\n", _dataLines));
        _name = null;
        _dataLines = null;
        return @event;
    }
}

/// <summary>What one streamed event means for the reply.</summary>
public readonly record struct ModelStreamEvent(ModelStreamEventKind Kind, string Text = "")
{
    public static ModelStreamEvent TextOf(string text) => new(ModelStreamEventKind.Text, text);
    public static readonly ModelStreamEvent Recognized = new(ModelStreamEventKind.Recognized);
    public static readonly ModelStreamEvent Done = new(ModelStreamEventKind.Done);
    public static ModelStreamEvent Failure(string message) => new(ModelStreamEventKind.Failure, message);
    public static readonly ModelStreamEvent Unrecognized = new(ModelStreamEventKind.Unrecognized);
}

public enum ModelStreamEventKind
{
    Text,
    /// <summary>An event of this format that carries no text (a start, a stop reason, a ping).</summary>
    Recognized,
    Done,
    Failure,
    /// <summary>Not an event of this format: a sign the configured format is wrong.</summary>
    Unrecognized,
}

/// <summary>
/// Sends requests to the configured model service in any of its three formats and yields the
/// reply's text as it streams. Ported from upstream ModelService.swift.
/// </summary>
public sealed class ModelServiceClient(HttpClient? httpClient = null)
{
    public HttpClient Session { get; } = httpClient ?? new HttpClient();

    public bool IsConfigured(CidaSettings settings) => settings.IsModelServiceComplete;

    /// <summary>
    /// The request <paramref name="settings"/> would send for <paramref name="request"/>;
    /// throws when the configuration is not complete.
    /// </summary>
    public static PreparedModelRequest Prepare(
        ProcessingRequest request, CidaSettings settings, TimeSpan? timeout = null)
    {
        var missing = settings.ModelService.MissingFields(hasApiKey: settings.ApiKey.Length > 0);
        if (missing.Count > 0)
        {
            throw new ModelServiceException(ModelServiceErrorInfo.IncompleteConfiguration(missing));
        }
        var prompt = ModelPromptBuilder.Build(request, settings);
        return ModelRequestBuilder.Build(prompt, settings.ModelService, settings.ApiKey,
            timeout ?? TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Posts <paramref name="prepared"/> and hands every piece of reply text to
    /// <paramref name="onText"/>, whether the service streams server-sent events or answers
    /// with one JSON document. <paramref name="transcript"/> keeps the status and the raw
    /// reply, with the key redacted, for <c>check --verbose</c>.
    /// </summary>
    public async Task SendAsync(
        PreparedModelRequest prepared,
        ModelRequestFormat format,
        SecretRedactor redactor,
        ModelResponseTranscript? transcript = null,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default)
    {
        onText ??= _ => { };
        HttpResponseMessage response;
        try
        {
            response = await Session.SendAsync(
                prepared.HttpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelServiceException(ModelServiceErrorInfo.Transport(error));
        }
        using (response)
        {
            transcript?.Record((int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                var text = redactor.Redact(await CollectText(response));
                transcript?.Append(text);
                throw new ModelServiceException(ModelServiceErrorInfo.Http(
                    (int)response.StatusCode,
                    ModelServiceClient.ProviderText(JsonValue.Parse(text)),
                    text));
            }

            var contentTypeHeader = response.Content.Headers.ContentType?.ToString()?.ToLowerInvariant() ?? "";
            if (contentTypeHeader.Contains("text/event-stream"))
            {
                await ReceiveEventsAsync(
                    response, format, redactor, transcript, onText, cancellationToken);
            }
            else
            {
                var text = await CollectText(response);
                transcript?.Append(redactor.Redact(text));
                var document = JsonValue.Parse(text);
                if (document == null)
                {
                    throw new ModelServiceException(ModelServiceErrorInfo.UnexpectedResponse(
                        "服务商返回的不是 JSON，也不是事件流。"));
                }
                var providerMessage = ModelServiceClient.ProviderText(document);
                if (providerMessage != null && document["error"] != null)
                {
                    throw new ModelServiceException(
                        ModelServiceErrorInfo.Provider(redactor.Redact(providerMessage)));
                }
                var reply = CompleteReply(format, document);
                if (reply == null)
                {
                    throw new ModelServiceException(ModelServiceErrorInfo.UnexpectedResponse(
                        $"返回的 JSON 里没有 {format.RawValue()} 格式的回复。"));
                }
                if (reply.Length == 0)
                {
                    throw new ModelServiceException(
                        new ModelServiceErrorInfo(ModelServiceError.EmptyResult));
                }
                onText(reply);
            }
        }
    }

    private static async Task<string> CollectText(HttpResponseMessage response)
    {
        // Capped at 1 MB: an error page is never larger in practice.
        var stream = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0) break;
            var remaining = 1_000_000 - (int)memory.Length;
            memory.Write(buffer, 0, Math.Min(read, remaining));
            if (memory.Length >= 1_000_000) break;
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task ReceiveEventsAsync(
        HttpResponseMessage response,
        ModelRequestFormat format,
        SecretRedactor redactor,
        ModelResponseTranscript? transcript,
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var parser = new ServerSentEventParser();
        var recognizedAnyEvent = false;
        var done = false;

        async Task HandleLine(string lineText)
        {
            var text = lineText;
            if (text.EndsWith('\r')) text = text[..^1];
            transcript?.Append(redactor.Redact(text) + "\n");
            var handled = parser.Consume(text);
            if (handled != null) Handle(handled);
        }

        void Handle(ServerSentEvent @event)
        {
            var streamEvent = StreamEvent(format, @event);
            switch (streamEvent.Kind)
            {
                case ModelStreamEventKind.Text:
                    recognizedAnyEvent = true;
                    onText(streamEvent.Text);
                    break;
                case ModelStreamEventKind.Recognized:
                    recognizedAnyEvent = true;
                    break;
                case ModelStreamEventKind.Done:
                    done = true;
                    break;
                case ModelStreamEventKind.Failure:
                    throw new ModelServiceException(
                        ModelServiceErrorInfo.Provider(redactor.Redact(streamEvent.Text)));
                case ModelStreamEventKind.Unrecognized:
                    break;
            }
        }

        var line = new List<byte>();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                var byteValue = buffer[index];
                if (byteValue != 0x0A)
                {
                    line.Add(byteValue);
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                await HandleLine(Encoding.UTF8.GetString(line.ToArray()));
                line.Clear();
                if (done) return;
            }
        }
        if (line.Count > 0)
        {
            await HandleLine(Encoding.UTF8.GetString(line.ToArray()));
            if (done) return;
        }
        if (parser.Finish() is { } last)
        {
            Handle(last);
            if (done) return;
        }
        if (!recognizedAnyEvent)
        {
            throw new ModelServiceException(ModelServiceErrorInfo.UnexpectedResponse(
                $"事件流里没有 {format.RawValue()} 格式的事件。"));
        }
    }

    // MARK: Format mapping

    public static ModelStreamEvent StreamEvent(ModelRequestFormat format, ServerSentEvent @event)
    {
        if (format == ModelRequestFormat.ChatCompletions && @event.Data == "[DONE]")
        {
            return ModelStreamEvent.Done;
        }
        var payload = JsonValue.Parse(@event.Data);
        if (payload == null) return ModelStreamEvent.Unrecognized;
        switch (format)
        {
            case ModelRequestFormat.ChatCompletions:
            {
                if (payload["error"] != null)
                {
                    return ModelStreamEvent.Failure(
                        ModelServiceClient.ProviderText(payload) ?? @event.Data);
                }
                var choices = payload["choices"]?.ArrayValue;
                if (choices == null) return ModelStreamEvent.Unrecognized;
                var text = choices.FirstOrDefault()?["delta"]?["content"]?.StringValue;
                if (text is { Length: > 0 }) return ModelStreamEvent.TextOf(text);
                return ModelStreamEvent.Recognized;
            }
            case ModelRequestFormat.Responses:
            {
                var type = payload["type"]?.StringValue ?? @event.Name ?? "";
                switch (type)
                {
                    case "response.output_text.delta":
                    {
                        var text = payload["delta"]?.StringValue ?? "";
                        return text.Length == 0 ? ModelStreamEvent.Recognized : ModelStreamEvent.TextOf(text);
                    }
                    case "response.completed":
                        return ModelStreamEvent.Done;
                    case "response.failed":
                        return ModelStreamEvent.Failure(
                            payload["response"]?["error"]?["message"]?.StringValue ?? "服务商报告响应失败。");
                    case "error":
                        return ModelStreamEvent.Failure(
                            ModelServiceClient.ProviderText(payload) ?? @event.Data);
                    default:
                        return type.StartsWith("response.")
                            ? ModelStreamEvent.Recognized
                            : ModelStreamEvent.Unrecognized;
                }
            }
            case ModelRequestFormat.AnthropicMessages:
            {
                var type = payload["type"]?.StringValue ?? @event.Name ?? "";
                switch (type)
                {
                    case "content_block_delta":
                    {
                        if (payload["delta"]?["type"]?.StringValue != "text_delta") return ModelStreamEvent.Recognized;
                        var text = payload["delta"]?["text"]?.StringValue;
                        if (string.IsNullOrEmpty(text)) return ModelStreamEvent.Recognized;
                        return ModelStreamEvent.TextOf(text);
                    }
                    case "message_stop":
                        return ModelStreamEvent.Done;
                    case "error":
                        return ModelStreamEvent.Failure(
                            ModelServiceClient.ProviderText(payload) ?? @event.Data);
                    case "message_start" or "content_block_start" or "content_block_stop"
                        or "message_delta" or "ping":
                        return ModelStreamEvent.Recognized;
                    default:
                        return ModelStreamEvent.Unrecognized;
                }
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>The reply in a non-streamed response, or null when the document is not this format's.</summary>
    public static string? CompleteReply(ModelRequestFormat format, JsonValue document)
    {
        switch (format)
        {
            case ModelRequestFormat.ChatCompletions:
            {
                var choices = document["choices"]?.ArrayValue;
                if (choices == null) return null;
                return choices.FirstOrDefault()?["message"]?["content"]?.StringValue ?? "";
            }
            case ModelRequestFormat.Responses:
            {
                var direct = document["output_text"]?.StringValue;
                if (direct != null) return direct;
                var output = document["output"]?.ArrayValue;
                if (output == null) return null;
                var parts = new List<string>();
                foreach (var item in output)
                {
                    if (item["type"]?.StringValue != "message") continue;
                    var content = item["content"]?.ArrayValue;
                    if (content == null) continue;
                    foreach (var part in content)
                    {
                        if (part["type"]?.StringValue == "output_text"
                            && part["text"]?.StringValue is { } text)
                        {
                            parts.Add(text);
                        }
                    }
                }
                return string.Concat(parts);
            }
            case ModelRequestFormat.AnthropicMessages:
            {
                var content = document["content"]?.ArrayValue;
                if (content == null) return null;
                return string.Concat(content
                    .Where(item => item["type"]?.StringValue == "text")
                    .Select(item => item["text"]?.StringValue ?? ""));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// The message a provider put in an error document: OpenAI and Anthropic nest it under
    /// <c>error.message</c>; others use <c>error</c>, <c>message</c> or <c>detail</c>.
    /// </summary>
    public static string? ProviderText(JsonValue? document) =>
        document?["error"]?["message"]?.StringValue
        ?? document?["error"]?.StringValue
        ?? document?["message"]?.StringValue
        ?? document?["detail"]?.StringValue;
}

// MARK: - Request

/// <summary>A request ready to send, and the parts of it <c>check --verbose</c> shows.</summary>
public sealed class PreparedModelRequest
{
    public sealed record Header(string Name, string Value);

    public required HttpRequestMessage HttpRequest { get; init; }
    public required JsonValue Body { get; init; }

    /// <summary>
    /// The headers that say something about the configuration, with the key replaced by ••••.
    /// </summary>
    public required IReadOnlyList<Header> DisplayHeaders { get; init; }

    public Uri Url => HttpRequest.RequestUri!;
    public string Method => HttpRequest.Method.Method;
}

public static class ModelRequestBuilder
{
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>Anthropic Messages requires <c>max_tokens</c>; <c>body</c> can change it.</summary>
    public const long AnthropicMaxTokens = 8192;

    public static PreparedModelRequest Build(
        ModelPrompt prompt,
        ModelConfiguration configuration,
        string apiKey,
        TimeSpan timeout)
    {
        var endpoint = configuration.EndpointUrl
            ?? throw new ModelServiceException(ModelServiceErrorInfo.IncompleteConfiguration(["endpoint"]));
        var body = JsonValue.Object(BaseBody(prompt, configuration))
            .Merging(JsonValue.Object(configuration.Body));

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var redactor = new SecretRedactor(apiKey);
        var displayHeaders = new List<PreparedModelRequest.Header>();

        void Set(string value, string name)
        {
            request.Headers.Remove(name);
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? value["Bearer ".Length..].Trim()
                        : value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            var header = new PreparedModelRequest.Header(name, redactor.Redact(value));
            var index = displayHeaders.FindIndex(existing =>
                existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                displayHeaders[index] = header;
            }
            else
            {
                displayHeaders.Add(header);
            }
        }

        if (apiKey.Length > 0 && configuration.ResolvedAuth.HeaderName() is { } headerName)
        {
            Set(configuration.ResolvedAuth.HeaderValue(apiKey), headerName);
        }
        if (configuration.Format == ModelRequestFormat.AnthropicMessages)
        {
            Set(AnthropicVersion, "anthropic-version");
        }
        foreach (var name in configuration.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            Set(configuration.Headers[name], name);
        }
        request.Content = new StringContent(body.CompactText, Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        // Apply the timeout via HttpClient instead: kept here for parity of the surfaced value.
        return new PreparedModelRequest
        {
            HttpRequest = request,
            Body = body,
            DisplayHeaders = displayHeaders,
        };
    }

    /// <summary>The body each format expects, in the order its documentation writes it.</summary>
    public static JsonObject BaseBody(ModelPrompt prompt, ModelConfiguration configuration)
    {
        var model = JsonValue.String(configuration.Model);
        var userMessage = JsonValue.Object(new JsonObject
        {
            ["role"] = JsonValue.String("user"),
            ["content"] = JsonValue.String(prompt.UserMessage),
        });
        switch (configuration.Format)
        {
            case ModelRequestFormat.ChatCompletions:
                return new JsonObject
                {
                    ["model"] = model,
                    ["stream"] = JsonValue.Bool(true),
                    ["messages"] = JsonValue.Array(
                    [
                        JsonValue.Object(new JsonObject
                        {
                            ["role"] = JsonValue.String("system"),
                            ["content"] = JsonValue.String(prompt.SystemMessage),
                        }),
                        userMessage,
                    ]),
                };
            case ModelRequestFormat.Responses:
                // Cida keeps no record of requests, so the service is asked not to either.
                return new JsonObject
                {
                    ["model"] = model,
                    ["stream"] = JsonValue.Bool(true),
                    ["store"] = JsonValue.Bool(false),
                    ["instructions"] = JsonValue.String(prompt.SystemMessage),
                    ["input"] = JsonValue.Array([userMessage]),
                };
            case ModelRequestFormat.AnthropicMessages:
                return new JsonObject
                {
                    ["model"] = model,
                    ["max_tokens"] = JsonValue.Integer(AnthropicMaxTokens),
                    ["stream"] = JsonValue.Bool(true),
                    ["system"] = JsonValue.String(prompt.SystemMessage),
                    ["messages"] = JsonValue.Array([userMessage]),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }
}

// MARK: - Transcript

/// <summary>
/// The status and raw reply of one request, kept for <c>check --verbose</c>. The text is
/// already redacted when it arrives and is capped, since a streamed reply can be long.
/// </summary>
public sealed class ModelResponseTranscript
{
    public const int CharacterLimit = 16_000;

    private readonly object _lock = new();
    private int? _status;
    private string _text = "";

    public int? StatusCode { get { lock (_lock) return _status; } }
    public string Body { get { lock (_lock) return _text; } }

    public void Record(int statusCode)
    {
        lock (_lock) _status = statusCode;
    }

    public void Append(string part)
    {
        lock (_lock)
        {
            if (_text.Length >= CharacterLimit) return;
            var remaining = CharacterLimit - _text.Length;
            _text += part.Length <= remaining ? part : part[..remaining];
        }
    }
}

// MARK: - Errors and redaction

public enum ModelServiceError
{
    /// <summary>The configuration lacks these fields (command-line names).</summary>
    IncompleteConfiguration,

    InvalidRequest,

    /// <summary>A non-2xx status; the body is the provider's text with the key redacted.</summary>
    Http,

    /// <summary>An error event in a successful stream, or an error document.</summary>
    Provider,

    /// <summary>A reply that is not in the configured format.</summary>
    UnexpectedResponse,

    EmptyResult,

    Transport,
}

public sealed record ModelServiceErrorInfo(
    ModelServiceError Kind,
    IReadOnlyList<string>? Missing = null,
    int? Status = null,
    string? ProviderMessage = null,
    string? Body = null,
    string? Message = null,
    Exception? TransportError = null)
{
    public int? StatusCode => Kind == ModelServiceError.Http ? Status : null;

    /// <summary>The panel's failure note: <c>请求失败：&lt;this&gt; 按 ⏎ 重试</c>.</summary>
    public string Description() => Kind switch
    {
        ModelServiceError.IncompleteConfiguration => "还没配置模型服务，在设置里复制配置提示词交给 AI 助手。",
        ModelServiceError.InvalidRequest => "无法构造模型请求。",
        ModelServiceError.Http => ProviderMessage ?? $"模型服务请求失败（HTTP {Status}）。",
        ModelServiceError.Provider => Message ?? "",
        ModelServiceError.UnexpectedResponse => "模型服务返回了无法识别的响应。",
        ModelServiceError.EmptyResult => "模型服务没有返回文本。",
        ModelServiceError.Transport => $"连不上模型服务：{TransportError?.Message}",
        _ => "",
    };

    /// <summary>A short cause for the check's report and Settings' failure note.</summary>
    public string Reason() => Kind switch
    {
        ModelServiceError.IncompleteConfiguration
            => $"配置还不完整，缺少 {string.Join("、", Missing ?? [])}",
        ModelServiceError.InvalidRequest => "无法构造请求",
        ModelServiceError.Http => Status switch
        {
            401 or 403 => "服务商拒绝了 API Key",
            404 => "找不到端点或模型",
            429 => "请求太频繁或额度不足",
            >= 400 and < 500 => "服务商拒绝了请求",
            _ => "服务商出错",
        },
        ModelServiceError.Provider => "服务商返回了错误",
        ModelServiceError.UnexpectedResponse => "返回的格式与 format 不符",
        ModelServiceError.EmptyResult => "没有返回文本",
        ModelServiceError.Transport => TransportError switch
        {
            TaskCanceledException => "请求超时",
            TimeoutException => "请求超时",
            _ => "网络出错",
        },
        _ => "",
    };

    /// <summary>What the provider said, for <c>check --verbose</c>.</summary>
    public string? ProviderText() => Kind switch
    {
        ModelServiceError.Http => Body,
        ModelServiceError.Provider => Message,
        ModelServiceError.UnexpectedResponse => Message,
        ModelServiceError.Transport => TransportError?.Message,
        _ => null,
    };

    public static ModelServiceErrorInfo IncompleteConfiguration(IReadOnlyList<string> missing) =>
        new(ModelServiceError.IncompleteConfiguration, Missing: missing);

    public static ModelServiceErrorInfo Http(int status, string? providerMessage, string body) =>
        new(ModelServiceError.Http, Status: status, ProviderMessage: providerMessage, Body: body);

    public static ModelServiceErrorInfo Provider(string message) =>
        new(ModelServiceError.Provider, Message: message);

    public static ModelServiceErrorInfo UnexpectedResponse(string detail) =>
        new(ModelServiceError.UnexpectedResponse, Message: detail);

    public static ModelServiceErrorInfo Transport(Exception error) =>
        new(ModelServiceError.Transport, TransportError: error);
}

public sealed class ModelServiceException(ModelServiceErrorInfo error) : Exception(error.Description())
{
    public ModelServiceErrorInfo Error { get; } = error;
}

/// <summary>
/// Replaces the API key with •••• in anything Cida prints or shows, including a provider's
/// echo of it inside JSON or a URL.
/// </summary>
public sealed record SecretRedactor(string Secret)
{
    public const string Mask = "••••";

    public string Redact(string text)
    {
        // Anything shorter cannot be a key, and replacing it would garble the text.
        if (Secret.Length < 4) return text;
        var result = text;
        foreach (var form in Forms())
        {
            if (result.Contains(form))
            {
                result = result.Replace(form, Mask);
            }
        }
        return result;
    }

    private IReadOnlyList<string> Forms()
    {
        var forms = new List<string> { Secret };
        var jsonEscaped = JsonValue.String(Secret).CompactText[1..^1];
        var slashEscaped = jsonEscaped.Replace("/", "\\/");
        string percentEncoded;
        try
        {
            percentEncoded = Uri.EscapeDataString(Secret);
        }
        catch
        {
            percentEncoded = Secret;
        }
        foreach (var form in new[] { jsonEscaped, slashEscaped, percentEncoded })
        {
            if (!forms.Contains(form)) forms.Add(form);
        }
        return forms;
    }
}
