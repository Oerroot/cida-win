namespace Cida.Core;

/// <summary>
/// One real request with the current configuration: the model is asked to translate "hello"
/// into Chinese through the same prompt contract and request path the panel uses. Ported
/// from upstream ModelServiceCheck.swift.
/// </summary>
public static class ModelServiceCheck
{
    public const string Source = "hello";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<ModelServiceCheckResult> RunAsync(
        CidaSettings settings,
        ModelServiceClient? client = null,
        Func<DateTime>? now = null,
        CancellationToken cancellationToken = default)
    {
        now ??= static () => DateTime.Now;
        client ??= new ModelServiceClient();
        var startedAt = now();
        var redactor = new SecretRedactor(settings.ApiKey);
        var transcript = new ModelResponseTranscript();
        PreparedModelRequest? prepared = null;
        var reply = "";
        ModelServiceErrorInfo? failure = null;
        try
        {
            var request = new ProcessingRequest
            {
                Text = Source,
                Mode = ProcessingMode.Translate,
                MyLanguage = "简体中文",
                ForeignLanguage = "English",
            };
            prepared = ModelServiceClient.Prepare(request, settings, Timeout);
            var collected = "";
            await client.SendAsync(
                prepared,
                settings.ModelService.Format,
                redactor,
                transcript,
                text => collected += text,
                cancellationToken);
            reply = redactor.Redact(collected.Trim());
            if (reply.Length == 0) failure = ModelServiceErrorInfo.UnexpectedResponse("");
            if (reply.Length == 0)
            {
                failure = new ModelServiceErrorInfo(ModelServiceError.EmptyResult);
            }
        }
        catch (ModelServiceException error)
        {
            failure = error.Error;
        }
        catch (OperationCanceledException)
        {
            failure = new ModelServiceErrorInfo(ModelServiceError.Transport, TransportError: new TimeoutException("请求超时"));
        }
        catch (Exception error)
        {
            failure = ModelServiceErrorInfo.UnexpectedResponse(redactor.Redact(error.Message));
        }
        var finishedAt = now();
        return new ModelServiceCheckResult(
            settings.ModelService.Model,
            finishedAt - startedAt,
            reply,
            failure,
            prepared,
            transcript.StatusCode,
            transcript.Body,
            new ModelServiceCheckRecord(
                failure == null,
                failure?.StatusCode,
                failure?.Reason(),
                finishedAt,
                settings.ModelServiceFingerprint));
    }
}

public sealed record ModelServiceCheckResult(
    string Model,
    TimeSpan Duration,
    /// <summary>The model's reply, trimmed and redacted.</summary>
    string Reply,
    ModelServiceErrorInfo? Failure,
    /// <summary>What was sent; null when the configuration was incomplete.</summary>
    PreparedModelRequest? Request,
    int? ResponseStatus,
    /// <summary>The raw reply, redacted and capped.</summary>
    string ResponseBody,
    ModelServiceCheckRecord Record)
{
    public bool Passed => Failure == null;
}

/// <summary>What Settings' 模型服务 row says under its label.</summary>
public enum ModelServiceStatus
{
    Ready,
    Checking,

    /// <summary>The latest check of this configuration failed: <c>401 · 服务商拒绝了 API Key</c>.</summary>
    Failed,
}

public static class ModelServiceStatusExtensions
{
    public static string Caption(this ModelServiceStatus status) => status switch
    {
        ModelServiceStatus.Ready => "已就绪",
        ModelServiceStatus.Checking => "正在检查…",
        ModelServiceStatus.Failed => "检查失败",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Only 已就绪 gets the accent dot.</summary>
    public static bool IsReady(this ModelServiceStatus status) => status == ModelServiceStatus.Ready;
}
