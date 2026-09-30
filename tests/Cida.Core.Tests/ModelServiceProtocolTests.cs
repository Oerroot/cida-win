using System.Net;
using System.Text;
using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

/// <summary>
/// Drives ModelServiceClient against an in-process HTTP listener, the way the upstream tests
/// drive it against a local server: three formats, streaming and complete replies.
/// </summary>
public sealed class ModelServiceProtocolTests
{

    private static readonly SemaphoreSlim ServerPortLock = new(1, 1);

    private static async Task WithServerAsync(
        Func<HttpListenerRequest, (int Status, string ContentType, string Body)> responder,
        Func<Uri, Task> action)
    {
        await ServerPortLock.WaitAsync();
        try
        {
            var port = FindFreePort();
            var uri = new Uri($"http://127.0.0.1:{port}/v1/chat");
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        var (status, contentType, body) = responder(context.Request);
                        context.Response.StatusCode = status;
                        context.Response.ContentType = contentType;
                        context.Response.SendChunked = true;
                        var bytes = Encoding.UTF8.GetBytes(body);
                        await context.Response.OutputStream.WriteAsync(bytes);
                        await context.Response.OutputStream.FlushAsync();
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // listener stopped
                    }
                }
            });
            try
            {
                await action(uri);
            }
            finally
            {
                listener.Stop();
                try { await serve; } catch { /* shutdown race */ }
            }
        }
        finally
        {
            ServerPortLock.Release();
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static CidaSettings SettingsFor(
        Uri url, ModelRequestFormat format, string apiKey = "sk-test-1234") => new()
    {
        ModelService = new ModelConfiguration
        {
            Endpoint = url.ToString(),
            Format = format,
            Model = "test-model",
        },
        ApiKey = apiKey,
    };

    private static ProcessingRequest Request() => new()
    {
        Text = "hello world",
        Mode = ProcessingMode.Translate,
        MyLanguage = "简体中文",
        ForeignLanguage = "English",
    };

    // MARK: Request building

    [Fact]
    public async Task ChatCompletionsRequestBodyHasModelStreamAndMessages()
    {
        await WithServerAsync(
            request =>
            {
                var body = request.BodyString();
                Assert.Contains("\"model\":\"test-model\"", body);
                Assert.Contains("\"stream\":true", body);
                Assert.Contains("hello world", body);
                return (200, "application/json",
                    "{\"choices\":[{\"message\":{\"content\":\"你好世界\"}}]}");
            },
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var text = "";
                await client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                    new SecretRedactor(settings.ApiKey), null, piece => text += piece);
                Assert.Equal("你好世界", text);
            });
    }

    [Fact]
    public async Task ChatCompletionsStreamsDeltasUntilDone()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}",
            "",
            "data: [DONE]",
            "");
        await WithServerAsync(
            _ => (200, "text/event-stream", sse),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var pieces = new List<string>();
                await client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                    new SecretRedactor(settings.ApiKey), null, pieces.Add);
                Assert.Equal(["你", "好"], pieces);
            });
    }

    [Fact]
    public async Task ResponsesFormatStreamsOutputTextDeltas()
    {
        var sse = string.Join("\n",
            "event: response.created",
            "data: {\"type\":\"response.created\"}",
            "",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}",
            "",
            "data: {\"type\":\"response.completed\"}");
        await WithServerAsync(
            _ => (200, "text/event-stream", sse),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.Responses);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var body = prepared.Body.CompactText;
                Assert.Contains("\"store\":false", body);
                Assert.Contains("\"instructions\":", body);
                var text = "";
                await client.SendAsync(prepared, ModelRequestFormat.Responses,
                    new SecretRedactor(settings.ApiKey), null, piece => text += piece);
                Assert.Equal("Hi", text);
            });
    }

    [Fact]
    public async Task AnthropicMessagesSendsVersionHeaderAndMaxTokens()
    {
        string? authorization = null;
        string? apiKeyHeader = null;
        string? version = null;
        await WithServerAsync(
            request =>
            {
                authorization = request.Headers["Authorization"];
                apiKeyHeader = request.Headers["x-api-key"];
                version = request.Headers["anthropic-version"];
                return (200, "application/json",
                    "{\"content\":[{\"type\":\"text\",\"text\":\"你好\"}]}");
            },
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.AnthropicMessages);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                Assert.Contains("\"max_tokens\":8192", prepared.Body.CompactText);
                await client.SendAsync(prepared, ModelRequestFormat.AnthropicMessages,
                    new SecretRedactor(settings.ApiKey));
                Assert.Null(authorization);
                Assert.Equal("sk-test-1234", apiKeyHeader);
                Assert.Equal("2023-06-01", version);
            });
    }

    [Fact]
    public async Task AnthropicMessagesStreamsTextDeltasUntilMessageStop()
    {
        var sse = string.Join("\n",
            "data: {\"type\":\"message_start\"}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}",
            "",
            "data: {\"type\":\"message_stop\"}");
        await WithServerAsync(
            _ => (200, "text/event-stream", sse),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.AnthropicMessages);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var text = "";
                await client.SendAsync(prepared, ModelRequestFormat.AnthropicMessages,
                    new SecretRedactor(settings.ApiKey), null, piece => text += piece);
                Assert.Equal("你好", text);
            });
    }

    // MARK: Errors

    [Fact]
    public async Task HttpErrorSurfacesProviderMessageRedacted()
    {
        await WithServerAsync(
            _ => (401, "application/json",
                "{\"error\":{\"message\":\"Incorrect API key provided: sk-test-1234\"}}"),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
                    client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                        new SecretRedactor(settings.ApiKey)));
                Assert.Equal(ModelServiceError.Http, error.Error.Kind);
                Assert.Equal(401, error.Error.StatusCode);
                Assert.DoesNotContain("sk-test-1234", error.Error.Description());
                Assert.Contains(SecretRedactor.Mask, error.Error.Description());
                Assert.Equal("服务商拒绝了 API Key", error.Error.Reason());
            });
    }

    [Fact]
    public async Task ErrorEventInStreamBecomesProviderFailure()
    {
        var sse = "data: {\"error\":{\"message\":\"quota exceeded\"}}\n\n";
        await WithServerAsync(
            _ => (200, "text/event-stream", sse),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
                    client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                        new SecretRedactor(settings.ApiKey)));
                Assert.Equal(ModelServiceError.Provider, error.Error.Kind);
                Assert.Equal("quota exceeded", error.Error.Message);
            });
    }

    [Fact]
    public async Task WrongFormatStreamIsUnexpectedResponse()
    {
        var sse = "data: {\"type\":\"unknown_event_family\"}\n\n";
        await WithServerAsync(
            _ => (200, "text/event-stream", sse),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
                    client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                        new SecretRedactor(settings.ApiKey)));
                Assert.Equal(ModelServiceError.UnexpectedResponse, error.Error.Kind);
            });
    }

    [Fact]
    public async Task EmptyReplyIsEmptyResult()
    {
        await WithServerAsync(
            _ => (200, "application/json", "{\"choices\":[{\"message\":{\"content\":\"\"}}]}"),
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
                    client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                        new SecretRedactor(settings.ApiKey)));
                Assert.Equal(ModelServiceError.EmptyResult, error.Error.Kind);
            });
    }

    // MARK: Body merging

    [Fact]
    public async Task ExtraBodyMergesIntoBaseBody()
    {
        await WithServerAsync(
            request =>
            {
                var body = request.BodyString();
                Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", body.Replace(" ", ""));
                return (200, "application/json",
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            },
            async url =>
            {
                var settings = SettingsFor(url, ModelRequestFormat.ChatCompletions) with
                {
                    ModelService = new ModelConfiguration
                    {
                        Endpoint = url.ToString(),
                        Format = ModelRequestFormat.ChatCompletions,
                        Model = "test-model",
                        Body = new JsonObject
                        {
                            ["thinking"] = JsonValue.Object(new JsonObject
                            {
                                ["type"] = JsonValue.String("disabled"),
                            }),
                        },
                    },
                };
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                await client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                    new SecretRedactor(settings.ApiKey));
            });
    }

    [Fact]
    public async Task LocalEndpointDoesNotRequireApiKey()
    {
        await WithServerAsync(
            _ => (200, "application/json", "{\"choices\":[{\"message\":{\"content\":\"hi\"}}]}"),
            async url =>
            {
                var settings = new CidaSettings
                {
                    ModelService = new ModelConfiguration
                    {
                        Endpoint = url.ToString(),
                        Model = "local-model",
                    },
                    ApiKey = "",
                };
                Assert.True(settings.IsModelServiceComplete);
                var client = new ModelServiceClient();
                var prepared = ModelServiceClient.Prepare(Request(), settings);
                var text = "";
                await client.SendAsync(prepared, ModelRequestFormat.ChatCompletions,
                    new SecretRedactor(settings.ApiKey), null, piece => text += piece);
                Assert.Equal("hi", text);
            });
    }

    [Fact]
    public void IncompleteConfigurationNamesMissingFields()
    {
        var settings = new CidaSettings
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.deepseek.com/chat/completions",
                Model = "",
            },
            ApiKey = "",
        };
        Assert.Equal(["model", "api-key"], settings.ModelService.MissingFields(false));
    }
}

file static class RequestBodyExtensions
{
    public static string BodyString(this HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
