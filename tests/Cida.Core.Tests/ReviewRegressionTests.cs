using System.Net;
using System.Net.Http;
using System.Text;
using Cida.Core;

namespace Cida.Core.Tests;

public sealed class ReviewRegressionTests
{
    private static readonly CidaSettings Settings = new()
    {
        ApiKey = "sk-fake-review-key",
        ModelService = new() { Endpoint = "http://127.0.0.1/chat", Model = "test-model" },
    };
    private static readonly ProcessingRequest Request = new()
    { Text = "hello", Mode = ProcessingMode.Translate, MyLanguage = "简体中文", ForeignLanguage = "English" };

    [Fact]
    public async Task BearerAuthenticationReachesTheTransportWithItsScheme()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Settings.ApiKey, request.Headers.Authorization?.Parameter);
            return Reply(new MemoryStream(Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}")));
        });
        using var http = new HttpClient(handler);
        await new ModelServiceClient(http).SendAsync(ModelServiceClient.Prepare(Request, Settings),
            ModelRequestFormat.ChatCompletions, new(Settings.ApiKey));
    }

    [Fact]
    public void ExplicitAuthorizationOverrideKeepsItsSchemeAndParameter()
    {
        var settings = Settings with { ModelService = Settings.ModelService with
        { Headers = new Dictionary<string, string> { ["Authorization"] = "Basic ZmFrZTpmYWtl" } } };
        var prepared = ModelServiceClient.Prepare(Request, settings);
        Assert.Equal("Basic", prepared.HttpRequest.Headers.Authorization?.Scheme);
        Assert.Equal("ZmFrZTpmYWtl", prepared.HttpRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void DisabledShortcutsSurviveSaveAndReload()
    {
        var disabled = Settings with { Shortcut = null, CaptureShortcut = null, LayerShortcut = null, ImproveShortcut = null };
        var restored = CidaSettings.FromJsonText(disabled.ToJsonText());
        Assert.Null(restored.Shortcut);
        Assert.Null(restored.CaptureShortcut);
        Assert.Null(restored.LayerShortcut);
        Assert.Null(restored.ImproveShortcut);
        Assert.Empty(restored.HeldShortcuts());
    }

    [Theory]
    [InlineData("application/json", 200)]
    [InlineData("application/json", 401)]
    [InlineData("text/event-stream", 200)]
    public async Task CancellationStopsBodyReadsAfterResponseHeaders(string contentType, int status)
    {
        using var stream = new WaitingStream();
        using var http = new HttpClient(new Handler(_ => Reply(stream, contentType, status)));
        using var cancellation = new CancellationTokenSource();
        var pieces = new List<string>();
        var task = new ModelServiceClient(http).SendAsync(ModelServiceClient.Prepare(Request, Settings),
            ModelRequestFormat.ChatCompletions, new(Settings.ApiKey), onText: pieces.Add,
            cancellationToken: cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(pieces);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task ConfiguredDeadlineCoversTheResponseBody(string contentType)
    {
        using var stream = new WaitingStream();
        using var http = new HttpClient(new Handler(_ => Reply(stream, contentType)));
        var task = new ModelServiceClient(http).SendAsync(
            ModelServiceClient.Prepare(Request, Settings, TimeSpan.FromMilliseconds(100)),
            ModelRequestFormat.ChatCompletions, new(Settings.ApiKey));
        var failure = await Assert.ThrowsAsync<ModelServiceException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(failure.Error.TransportError);
        Assert.Equal("请求超时", failure.Error.Reason());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ScreenshotCropMatchesSelectedPixelsAtEachScale(double scale)
    {
        var crop = CaptureCoordinates.Crop(100 / scale, 80 / scale, 300 / scale, 200 / scale,
            1920 / scale, 1080 / scale, 1920, 1080);
        Assert.Equal(new CaptureCoordinates.PixelBounds(100, 80, 300, 200), crop);
    }

    [Fact]
    public void CropClampsDraggingBeyondTheScreenEdges()
    {
        Assert.Equal(new CaptureCoordinates.PixelBounds(0, 0, 1920, 1080),
            CaptureCoordinates.Crop(-10, -20, 2000, 1200, 1920, 1080, 1920, 1080));
    }

    private static HttpResponseMessage Reply(Stream stream, string type = "application/json", int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
