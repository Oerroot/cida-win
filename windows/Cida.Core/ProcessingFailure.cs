namespace Cida.Core;

/// <summary>
/// Safe recovery categories for one failed request. Ported from upstream ProcessingFailure.swift
/// (#73): the panel shows a category and an explanation; provider prose, status codes and the
/// user's text stay in the command-line diagnostics instead of the panel.
/// </summary>
public enum ProcessingFailureCategory
{
    Timeout,
    Offline,

    /// <summary>The configuration, the prompt or the request itself needs a change.</summary>
    Configuration,

    /// <summary>The provider temporarily limited the request, e.g. HTTP 429.</summary>
    Limited,

    /// <summary>TLS or the certificate chain could not be verified.</summary>
    SecureConnection,

    Unknown,
}

/// <summary>
/// A failure with just enough safe information for recovery: what kind it was, which model was
/// configured, and the configuration fingerprint so an updated configuration is never diagnosed
/// with an older failure.
/// </summary>
public sealed record ProcessingFailure(
    ProcessingFailureCategory Category,
    string ModelName,
    string ConfigurationFingerprint)
{
    public static ProcessingFailure From(Exception error, CidaSettings settings) =>
        new(Classify(error), settings.ModelService.Model, settings.ModelServiceFingerprint);

    /// <summary>The panel's explanation; it never contains provider text or the user's text.</summary>
    public string Explanation => Category switch
    {
        ProcessingFailureCategory.Timeout => "请求超时。重试会从头执行，并替换已有输出。",
        ProcessingFailureCategory.Offline => "连不上模型服务。检查网络后重试；辞达不会自动重试。",
        ProcessingFailureCategory.Configuration =>
            "模型配置需要检查。在设置“模型”页复制配置提示词，交给 AI 助手继续检查；配置更新后即可重试。",
        ProcessingFailureCategory.Limited => "服务商暂时限制了请求。请稍后重试；一直出现时，到服务商后台查看用量与额度。",
        ProcessingFailureCategory.SecureConnection => "无法建立安全连接。检查网络或代理后重试；辞达不会跳过安全校验。",
        _ => "请求没有完成，可以重试。反复出现时，在设置“模型”页复制配置提示词，交给 AI 助手继续检查。",
    };

    /// <summary>Maps a thrown exception to a recovery category using status and typed codes only.</summary>
    public static ProcessingFailureCategory Classify(Exception error) => error switch
    {
        ModelServiceException failure => Classify(failure.Error),
        // The client turns its own request deadline into a transport error; cancellation by the
        // user is caught by the caller before it ever reaches this classification.
        TimeoutException => ProcessingFailureCategory.Timeout,
        OperationCanceledException => ProcessingFailureCategory.Timeout,
        HttpRequestException http => ClassifyTransport(http),
        System.IO.IOException => ProcessingFailureCategory.Offline,
        _ => ProcessingFailureCategory.Unknown,
    };

    public static ProcessingFailureCategory Classify(ModelServiceErrorInfo error)
    {
        switch (error.Kind)
        {
            case ModelServiceError.IncompleteConfiguration:
            case ModelServiceError.InvalidRequest:
            case ModelServiceError.UnexpectedResponse:
            case ModelServiceError.LengthRecoveryExhausted:
                return ProcessingFailureCategory.Configuration;
            case ModelServiceError.Http:
                if (error.IsLengthLimit) return ProcessingFailureCategory.Configuration;
                return error.Status switch
                {
                    401 or 403 or 404 => ProcessingFailureCategory.Configuration,
                    408 or 504 => ProcessingFailureCategory.Timeout,
                    429 => ProcessingFailureCategory.Limited,
                    _ => ProcessingFailureCategory.Unknown,
                };
            case ModelServiceError.Transport:
                return error.TransportError switch
                {
                    TimeoutException => ProcessingFailureCategory.Timeout,
                    HttpRequestException http => ClassifyTransport(http),
                    System.IO.IOException => ProcessingFailureCategory.Offline,
                    _ => ProcessingFailureCategory.Unknown,
                };
            default:
                return ProcessingFailureCategory.Unknown;
        }
    }

    private static ProcessingFailureCategory ClassifyTransport(HttpRequestException error) => error.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError
            => ProcessingFailureCategory.Offline,
        HttpRequestError.SecureConnectionError => ProcessingFailureCategory.SecureConnection,
        _ when error.InnerException is System.Security.Authentication.AuthenticationException
            => ProcessingFailureCategory.SecureConnection,
        _ => ProcessingFailureCategory.Unknown,
    };
}
