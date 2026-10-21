using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class ProcessingFailureTests
{
    private static CidaSettings Settings() => new()
    {
        ModelService = new ModelConfiguration
        {
            Endpoint = "https://api.example.com/v1/chat/completions",
            Format = ModelRequestFormat.ChatCompletions,
            Model = "deepseek-chat",
        },
        ApiKey = "sk-secret-key",
    };

    private static ModelServiceErrorInfo Http(int status, string body = "", string? providerMessage = null) =>
        ModelServiceErrorInfo.Http(status, providerMessage, body);

    private static ModelServiceException HttpError(int status, string body = "", string? providerMessage = null) =>
        new(Http(status, body, providerMessage));

    [Fact]
    public void CategoriesUseStatusAndTypedCodesNotProviderProse()
    {
        foreach (var status in new[] { 401, 403, 404 })
        {
            Assert.Equal(ProcessingFailureCategory.Configuration,
                ProcessingFailure.Classify(Http(status)));
        }
        Assert.Equal(ProcessingFailureCategory.Limited, ProcessingFailure.Classify(Http(429)));
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(Http(413)));
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(
            Http(400, """{"error":{"code":"context_length_exceeded"}}""")));
        Assert.Equal(ProcessingFailureCategory.Unknown,
            ProcessingFailure.Classify(Http(400, "input is too long")));
        Assert.Equal(ProcessingFailureCategory.Unknown, ProcessingFailure.Classify(Http(500)));
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(
            new ModelServiceErrorInfo(ModelServiceError.UnexpectedResponse, Message: "private")));
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(
            ModelServiceErrorInfo.IncompleteConfiguration(["api-key"])));
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(
            new ModelServiceErrorInfo(ModelServiceError.LengthRecoveryExhausted)));
        Assert.Equal(ProcessingFailureCategory.Timeout, ProcessingFailure.Classify(
            ModelServiceErrorInfo.Transport(new TimeoutException())));
        Assert.Equal(ProcessingFailureCategory.Offline, ProcessingFailure.Classify(
            ModelServiceErrorInfo.Transport(new HttpRequestException(HttpRequestError.ConnectionError))));
        Assert.Equal(ProcessingFailureCategory.SecureConnection, ProcessingFailure.Classify(
            ModelServiceErrorInfo.Transport(new HttpRequestException(HttpRequestError.SecureConnectionError))));
        Assert.Equal(ProcessingFailureCategory.Unknown, ProcessingFailure.Classify(
            ModelServiceErrorInfo.Provider("quota exceeded")));
        Assert.Equal(ProcessingFailureCategory.Unknown, ProcessingFailure.Classify(
            new ModelServiceErrorInfo(ModelServiceError.EmptyResult)));
    }

    [Fact]
    public void LengthLimitOnlyRecognizesExplicitTypedErrors()
    {
        Assert.True(Http(400, """{"error":{"code":"context_length_exceeded"}}""").IsLengthLimit);
        Assert.True(Http(400, """{"error":{"type":"request_too_large"}}""").IsLengthLimit);
        Assert.True(Http(413).IsLengthLimit);
        // Anthropic's typed 400 responses carry the length rejection only in the message.
        foreach (var message in new[]
        {
            "prompt is too long: 200 tokens > 100 maximum",
            "input length and max_tokens exceed context limit: 200 + 100 > 256",
            "This model's maximum context length is 100 tokens. However, you requested 200 tokens.",
        })
        {
            var body = """{"error":{"type":"invalid_request_error","message":"""
                + JsonValue.String(message).CompactText + "}}";
            Assert.True(Http(400, body).IsLengthLimit);
            // The same body under another status is not a typed length rejection.
            Assert.False(Http(429, body).IsLengthLimit);
        }
        Assert.False(Http(400, """{"error":{"type":"invalid_request_error","message":"max_tokens must be positive"}}""").IsLengthLimit);
        Assert.False(Http(400, "input is too long").IsLengthLimit);
        Assert.False(Http(400, "", "input too long").IsLengthLimit);
        Assert.False(Http(500).IsLengthLimit);
        Assert.False(new ModelServiceErrorInfo(ModelServiceError.Provider, Message: "context_length_exceeded").IsLengthLimit);
    }

    [Fact]
    public void FailureCarriesModelAndFingerprintWithoutProviderText()
    {
        var settings = Settings();
        const string providerText = "private source text from the provider";
        var failure = ProcessingFailure.From(
            HttpError(500, """{"error":{"message":"private source text from the provider"}}""", providerText),
            settings);
        Assert.Equal(ProcessingFailureCategory.Unknown, failure.Category);
        Assert.Equal("deepseek-chat", failure.ModelName);
        Assert.Equal(settings.ModelServiceFingerprint, failure.ConfigurationFingerprint);
        Assert.DoesNotContain(providerText, failure.Explanation);
        Assert.DoesNotContain(settings.ApiKey, failure.Explanation);

        foreach (var category in Enum.GetValues<ProcessingFailureCategory>())
        {
            var explanation = new ProcessingFailure(category, "m", "fp").Explanation;
            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.DoesNotContain(settings.ApiKey, explanation);
            Assert.DoesNotContain(providerText, explanation);
        }
    }

    [Fact]
    public void PanelStateFailKeepsTheStreamedTextAndRejectsLateUpdates()
    {
        var state = new PanelState();
        var serial = state.Begin("Original source", ProcessingAction.TranslationId, "fingerprint");
        Assert.True(state.Append(serial, "Partial output"));
        var failure = new ProcessingFailure(ProcessingFailureCategory.Timeout, "m", "fingerprint");

        Assert.True(state.Fail(serial, failure));
        Assert.Equal(ResultPhase.Failed, state.Phase);
        Assert.Equal(failure, state.Failure);
        Assert.Equal("Partial output", state.Result);
        // The same or an older request can neither append text nor finish after the failure.
        Assert.False(state.Append(serial, "late text"));
        Assert.False(state.Finish(serial, ResultPhase.Completed));
        Assert.False(state.Finish(serial - 1, ResultPhase.Failed));
        Assert.False(state.Fail(serial, failure));
        Assert.False(state.Fail(serial - 1, failure));
        Assert.Equal("Partial output", state.Result);
        Assert.Equal(failure, state.Failure);

        // A new request clears the previous failure and starts a fresh streaming result.
        var restarted = state.Begin("New source", ProcessingAction.TranslationId, "fingerprint");
        Assert.Null(state.Failure);
        Assert.Equal(ResultPhase.Streaming, state.Phase);
        Assert.Equal("", state.Result);
        Assert.True(restarted > serial);
        Assert.False(state.Fail(serial, failure));
        Assert.Null(state.Failure);
        Assert.Equal(ResultPhase.Streaming, state.Phase);
    }

    [Fact]
    public void PanelStateFinishKeepsItsExistingContract()
    {
        var state = new PanelState();
        var serial = state.Begin("source", ProcessingAction.TranslationId, "fp");
        Assert.True(state.Append(serial, "Result"));
        Assert.True(state.Finish(serial, ResultPhase.Completed, "note"));
        Assert.Equal(ResultPhase.Completed, state.Phase);
        Assert.Equal("note", state.Error);
        Assert.Null(state.Failure);
        // Finished requests refuse anything else.
        Assert.False(state.Append(serial, "more"));
        Assert.False(state.Fail(serial, new ProcessingFailure(ProcessingFailureCategory.Unknown, "m", "fp")));
        Assert.Equal("Result", state.Result);

        var stopped = new PanelState();
        var stoppedSerial = stopped.Begin("source", ProcessingAction.TranslationId, "fp");
        Assert.True(stopped.Append(stoppedSerial, "Part"));
        stopped.Stop();
        Assert.Equal(ResultPhase.Stopped, stopped.Phase);
        Assert.False(stopped.Finish(stoppedSerial, ResultPhase.Completed));
        Assert.False(stopped.Fail(stoppedSerial, new ProcessingFailure(ProcessingFailureCategory.Timeout, "m", "fp")));
        Assert.Equal("Part", stopped.Result);
    }
}
