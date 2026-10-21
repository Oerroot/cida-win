using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class SourcePartitionTests
{
    [Fact]
    public void ParagraphBoundaryKeepsTheBlankLineAsTheSeparator()
    {
        var split = SourcePartition.Split("First paragraph.\n\nSecond paragraph.");
        Assert.NotNull(split);
        Assert.Equal("First paragraph.", split!.First);
        Assert.Equal("\n\n", split.Separator);
        Assert.Equal("Second paragraph.", split.Second);
    }

    [Fact]
    public void SentenceAndWhitespaceBoundariesComeBeforeGraphemes()
    {
        var sentence = SourcePartition.Split("Hello. World");
        Assert.NotNull(sentence);
        Assert.Equal("Hello.", sentence!.First);
        Assert.Equal(" ", sentence.Separator);
        Assert.Equal("World", sentence.Second);

        var whitespace = SourcePartition.Split("abcdef ghijkl");
        Assert.NotNull(whitespace);
        Assert.Equal("abcdef", whitespace!.First);
        Assert.Equal(" ", whitespace.Separator);
        Assert.Equal("ghijkl", whitespace.Second);
    }

    [Fact]
    public void UnbrokenTextFallsBackToTheNearestGraphemeBoundary()
    {
        var split = SourcePartition.Split("abcdefghij");
        Assert.NotNull(split);
        Assert.Equal("abcde", split!.First);
        Assert.Equal("", split.Separator);
        Assert.Equal("fghij", split.Second);
    }

    [Theory]
    [InlineData("First paragraph.\n\nSecond paragraph.")]
    [InlineData("你好。接着处理下一句。再下一句。")]
    [InlineData("👨‍👩‍👧‍👦e\u0301👨‍👩‍👧‍👦e\u0301👨‍👩‍👧‍👦e\u0301")]
    [InlineData("aaaaaaaaaa")]
    [InlineData(" \n ")]
    [InlineData("hello world")]
    public void SplitIsLosslessForMixedScriptsAndExtendedGraphemes(string text)
    {
        var split = SourcePartition.Split(text);
        Assert.NotNull(split);
        Assert.Equal(text, split!.First + split.Separator + split.Second);
        Assert.False(string.IsNullOrEmpty(split.First));
        Assert.False(string.IsNullOrEmpty(split.Second));
        var total = SourcePartition.CountTextElements(text);
        // Both parts are strictly shorter, so every recursive segmentation makes progress.
        Assert.True(SourcePartition.CountTextElements(split.First) < total);
        Assert.True(SourcePartition.CountTextElements(split.Second) < total);
        Assert.Equal(total, SourcePartition.CountTextElements(split.First)
            + SourcePartition.CountTextElements(split.Separator)
            + SourcePartition.CountTextElements(split.Second));
        // The fallback boundary must never cut a surrogate pair or a combining sequence.
        AssertWellFormed(split.First);
        AssertWellFormed(split.Separator);
        AssertWellFormed(split.Second);
    }

    [Fact]
    public void SingleGraphemesAndEmptyTextCannotSplit()
    {
        Assert.Null(SourcePartition.Split(""));
        Assert.Null(SourcePartition.Split("a"));
        Assert.Null(SourcePartition.Split("👨‍👩‍👧‍👦"));
        // The family emoji plus a combining mark is still two text elements: "👨‍👩‍👧‍👦e" is not
        // one cluster, so it splits only at the grapheme boundary.
        Assert.NotNull(SourcePartition.Split("👨‍👩‍👧‍👦e\u0301"));
    }

    [Fact]
    public void PlaceholderReferencesStayIndivisibleWhenProtected()
    {
        Assert.Null(SourcePartition.Split("⟦1234567890⟧", preservingPlaceholders: true));
        VerifyPlaceholdersBalanced(SourcePartition.Split(
            string.Concat(Enumerable.Repeat("⟦123⟧", 8)), preservingPlaceholders: true), 8);
        VerifyPlaceholdersBalanced(SourcePartition.Split(
            "前缀文字⟦123⟧中间还有⟦45⟧以及更多文字", preservingPlaceholders: true), 2);
    }

    [Fact]
    public void WithoutProtectionTheSameTextCanSplitInsideTheReference()
    {
        // Only the translation layer asks for the protection; a general request may cut here.
        var split = SourcePartition.Split("⟦1234567890⟧");
        Assert.NotNull(split);
        Assert.Equal("⟦12345", split!.First);
        Assert.Equal("67890⟧", split.Second);
    }

    private static void VerifyPlaceholdersBalanced(SourcePartition? split, int expectedReferences)
    {
        Assert.NotNull(split);
        var joined = split!.First + split.Separator + split.Second;
        Assert.Equal(expectedReferences, joined.Count(character => character == '⟦'));
        Assert.Equal(expectedReferences, joined.Count(character => character == '⟧'));
        Assert.Equal(split.First.Count(character => character == '⟦'),
            split.First.Count(character => character == '⟧'));
        Assert.Equal(split.Second.Count(character => character == '⟦'),
            split.Second.Count(character => character == '⟧'));
    }

    private static void AssertWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]),
                    "a surrogate pair was cut");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[index]), "a surrogate pair was cut");
            }
        }
    }
}

public sealed class ModelServiceSegmentationTests
{
    [Theory]
    [InlineData(ModelRequestFormat.ChatCompletions)]
    [InlineData(ModelRequestFormat.Responses)]
    [InlineData(ModelRequestFormat.AnthropicMessages)]
    public async Task AllWireFormatsSplitRejectedInputAndKeepOneOrderedResult(ModelRequestFormat format)
    {
        using var server = new RecordingModelServer(format)
        {
            MaximumInputBytes = 120,
            EchoInput = true,
            StreamReply = true,
        };
        var settings = SettingsFor(server.Endpoint, format);
        var source = string.Join("\n\n", Enumerable.Range(1, 8)
            .Select(index => $"Paragraph {index}: 保留所有原文与次序，包括 emoji 👨‍👩‍👧‍👦。"));

        var output = "";
        await ModelServiceSegmentation.SendRecoveringLengthAsync(
            new ModelServiceClient(), TranslateRequest(source), settings, chunk => output += chunk);

        Assert.Equal(source, output);
        Assert.True(server.Requests.Count > 2, "the batch should have been segmented");
        Assert.True(server.Requests.Count < 80, "learn a smaller segment size instead of rejecting every sibling");
    }

    [Fact]
    public async Task OrdinaryFailuresNeverStartSegmentation()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            FailureStatus = 503,
            FailureBody = "unavailable",
        };
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions);
        var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
            ModelServiceSegmentation.SendRecoveringLengthAsync(
                new ModelServiceClient(),
                TranslateRequest(string.Concat(Enumerable.Repeat("text ", 100))),
                settings,
                _ => { }));
        Assert.Equal(503, error.Error.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task MinimumUnitThatStillExceedsTheLimitEndsRecoveryInsteadOfLooping()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            MaximumInputBytes = 0,
            EchoInput = true,
        };
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions);
        var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
            ModelServiceSegmentation.SendRecoveringLengthAsync(
                new ModelServiceClient(), TranslateRequest("abc"), settings, _ => { }));
        Assert.Equal(ModelServiceError.LengthRecoveryExhausted, error.Error.Kind);
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(error.Error));
        Assert.True(server.Requests.Count <= 3, $"made {server.Requests.Count} requests before giving up");
    }

    [Fact]
    public async Task CustomActionSegmentsThenAssemblesOneStructuredResult()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            MaximumInputBytes = 160,
            StructuredOutput = true,
        };
        var prompt = "Return one JSON object with an items array.";
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions) with
        {
            Actions = [new ProcessingAction { Id = "extract", Name = "Extract", Prompt = prompt }],
        };
        var source = string.Join("\n\n", Enumerable.Range(1, 10)
            .Select(index => $"Item{index} " + string.Concat(Enumerable.Repeat("context ", 6))));
        var request = new ProcessingRequest
        {
            Text = source,
            Mode = ProcessingMode.Custom,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
            ActionPrompt = prompt,
        };

        var output = "";
        var pieces = new List<string>();
        await ModelServiceSegmentation.SendRecoveringLengthAsync(
            new ModelServiceClient(), request, settings, chunk =>
            {
                output += chunk;
                pieces.Add(chunk);
            });

        var document = JsonValue.Parse(output);
        Assert.NotNull(document);
        var items = document!["items"]?.ArrayValue;
        Assert.NotNull(items);
        Assert.True(items!.Count > 1, "the partial results should have been assembled into one result");
        // One JSON document parsed above: concatenated independent replies would not parse.
        Assert.Single(pieces);
        Assert.Contains(server.Requests, recorded =>
            recorded.SystemMessage.Contains("Result assembly contract:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AssemblyThatCannotBeReducedEndsInLengthRecoveryExhausted()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            MaximumInputBytes = 40,
            StructuredOutput = true,
            RejectAssembly = true,
        };
        var prompt = "Return one JSON object with an items array.";
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions) with
        {
            Actions = [new ProcessingAction { Id = "extract", Name = "Extract", Prompt = prompt }],
        };
        var request = new ProcessingRequest
        {
            Text = string.Concat(Enumerable.Repeat("word ", 20)),
            Mode = ProcessingMode.Custom,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
            ActionPrompt = prompt,
        };
        var error = await Assert.ThrowsAsync<ModelServiceException>(() =>
            ModelServiceSegmentation.SendRecoveringLengthAsync(
                new ModelServiceClient(), request, settings, _ => { }));
        Assert.Equal(ModelServiceError.LengthRecoveryExhausted, error.Error.Kind);
        Assert.Equal(ProcessingFailureCategory.Configuration, ProcessingFailure.Classify(error.Error));
    }

    [Fact]
    public async Task CancellationStopsTheSegmentSequence()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            MaximumInputBytes = 60,
            EchoInput = true,
        };
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions);
        var source = string.Concat(Enumerable.Repeat("alpha beta. ", 12));
        using var cancellation = new CancellationTokenSource();
        var output = "";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelServiceSegmentation.SendRecoveringLengthAsync(
                new ModelServiceClient(),
                TranslateRequest(source),
                settings,
                chunk =>
                {
                    output += chunk;
                    if (output.Length > 0) cancellation.Cancel();
                },
                cancellation.Token));
        var requestsWhenCancelled = server.Requests.Count;
        Assert.True(requestsWhenCancelled > 0);
        await Task.Delay(250);
        Assert.Equal(requestsWhenCancelled, server.Requests.Count);
    }

    [Fact]
    public async Task LayerBatchKeepsIdsAndPlaceholdersWhenSplitting()
    {
        using var server = new RecordingModelServer(ModelRequestFormat.ChatCompletions)
        {
            MaximumInputBytes = 45,
            EchoInput = true,
        };
        var settings = SettingsFor(server.Endpoint, ModelRequestFormat.ChatCompletions);
        var source = new[]
        {
            "One short paragraph.",
            string.Concat(Enumerable.Repeat("Long paragraph. ", 12)),
            new string('a', 38) + "⟦123⟧" + new string('b', 38),
            "Final paragraph.",
        };

        var translated = await LayerTranslationRequest.TranslateWithLengthRecoveryAsync(
            new ModelServiceClient(), source, "English", settings);

        Assert.Equal(source.Select(text => text.Trim()).ToList(), translated);
        Assert.Equal(4, translated.Count);
        Assert.Contains("⟦123⟧", translated[2]);
        Assert.True(server.Requests.Count > 1);
    }

    private static CidaSettings SettingsFor(Uri endpoint, ModelRequestFormat format) => new()
    {
        ModelService = new ModelConfiguration
        {
            Endpoint = endpoint.ToString(),
            Format = format,
            Model = "test-model",
        },
        ApiKey = "",
    };

    private static ProcessingRequest TranslateRequest(string text) => new()
    {
        Text = text,
        Mode = ProcessingMode.Translate,
        MyLanguage = "简体中文",
        ForeignLanguage = "English",
    };
}

/// <summary>
/// A local HTTP endpoint shaped like the upstream test mock: three wire formats, request
/// recording, an optional byte limit that answers with each provider's typed length error, and
/// deterministic replies (echo, padded streaming echo, or a structured result for custom actions).
/// </summary>
internal sealed class RecordingModelServer : IDisposable
{
    internal sealed record Recorded(string RawBody, string UserContent, string SystemMessage);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpListener _listener;
    private readonly List<Recorded> _requests = [];
    private readonly Task _serve;

    public Uri Endpoint { get; }
    public ModelRequestFormat Format { get; }
    public int? MaximumInputBytes { get; init; }
    public bool EchoInput { get; init; }
    public bool StreamReply { get; init; }
    public bool StructuredOutput { get; init; }
    public bool RejectAssembly { get; init; }
    public int? FailureStatus { get; init; }
    public string? FailureBody { get; init; }
    public IReadOnlyList<Recorded> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public RecordingModelServer(ModelRequestFormat format)
    {
        Format = format;
        Gate.Wait();
        Uri? endpoint = null;
        try
        {
            HttpListener? listener = null;
            for (var attempt = 0; attempt < 20 && listener == null; attempt++)
            {
                var port = FreePort();
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    listener = candidate;
                    endpoint = new Uri($"http://127.0.0.1:{port}/v1/chat");
                }
                catch (HttpListenerException)
                {
                    candidate.Close();
                }
            }
            _listener = listener ?? throw new InvalidOperationException("could not start the test HTTP listener");
        }
        catch
        {
            Gate.Release();
            throw;
        }
        Endpoint = endpoint!;
        _serve = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // already gone
        }
        Gate.Release();
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch
            {
                try { context.Response.Abort(); } catch { }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string raw;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            raw = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        var (user, system) = ParseRequest(raw);
        lock (_requests) _requests.Add(new Recorded(raw, user, system));

        if (FailureStatus is int failureStatus)
        {
            Respond(context, failureStatus, "application/json", FailureBody ?? "");
            return;
        }
        if (MaximumInputBytes is int limit && Encoding.UTF8.GetByteCount(user) > limit)
        {
            // Each format's own typed length rejection, as providers document it.
            Respond(context, Format == ModelRequestFormat.Responses ? 413 : 400,
                "application/json", LengthErrorBody());
            return;
        }
        if (StructuredOutput)
        {
            if (RejectAssembly && system.Contains("Result assembly contract:", StringComparison.Ordinal))
            {
                // The assembly request is always too long: reducing groups must eventually fail
                // with LengthRecoveryExhausted instead of looping.
                Respond(context, 400, "application/json", LengthErrorBody());
                return;
            }
            Respond(context, 200, "application/json", CompleteBody(StructuredReply(user, system)));
            return;
        }
        if (EchoInput)
        {
            if (StreamReply)
            {
                Respond(context, 200, "text/event-stream", SseBody(Pad(user)));
            }
            else
            {
                Respond(context, 200, "application/json", CompleteBody(user));
            }
            return;
        }
        Respond(context, 200, "application/json", CompleteBody("ok"));
    }

    private (string User, string System) ParseRequest(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        switch (Format)
        {
            case ModelRequestFormat.ChatCompletions:
            {
                var messages = root.GetProperty("messages");
                var system = messages.EnumerateArray()
                    .FirstOrDefault(message => message.TryGetProperty("role", out var role)
                        && role.GetString() == "system");
                var user = messages[messages.GetArrayLength() - 1];
                return (ContentOf(user), ContentOf(system));
            }
            case ModelRequestFormat.Responses:
            {
                var system = root.TryGetProperty("instructions", out var instructions)
                    ? instructions.GetString() ?? ""
                    : "";
                var input = root.GetProperty("input");
                return (ContentOf(input[input.GetArrayLength() - 1]), system);
            }
            case ModelRequestFormat.AnthropicMessages:
            {
                var system = root.TryGetProperty("system", out var systemElement)
                    ? systemElement.GetString() ?? ""
                    : "";
                var messages = root.GetProperty("messages");
                return (ContentOf(messages[messages.GetArrayLength() - 1]), system);
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static string ContentOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";

    /// <summary>Models often add edge whitespace; the streaming recovery must drop it exactly once.</summary>
    private static string Pad(string text) => "\n  " + text + "  \n";

    private static IReadOnlyList<string> SplitForStream(string text)
    {
        if (text.Length <= 2) return [text];
        var middle = text.Length / 2;
        // Never break a surrogate pair between two reply chunks.
        if (char.IsLowSurrogate(text[middle])) middle--;
        if (middle <= 0) middle = 1;
        return [text[..middle], text[middle..]];
    }

    private string SseBody(string text)
    {
        var builder = new StringBuilder();
        void Data(string json) => builder.Append("data: ").Append(json).Append("\n\n");
        switch (Format)
        {
            case ModelRequestFormat.ChatCompletions:
                foreach (var chunk in SplitForStream(text))
                {
                    Data("{\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(chunk) + "}}]}");
                }
                Data("[DONE]");
                break;
            case ModelRequestFormat.Responses:
                foreach (var chunk in SplitForStream(text))
                {
                    Data("{\"type\":\"response.output_text.delta\",\"delta\":"
                        + JsonSerializer.Serialize(chunk) + "}");
                }
                Data("{\"type\":\"response.completed\"}");
                break;
            case ModelRequestFormat.AnthropicMessages:
                foreach (var chunk in SplitForStream(text))
                {
                    Data("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":"
                        + JsonSerializer.Serialize(chunk) + "}}");
                }
                Data("{\"type\":\"message_stop\"}");
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        return builder.ToString();
    }

    private string CompleteBody(string content)
    {
        var quoted = JsonSerializer.Serialize(content);
        return Format switch
        {
            ModelRequestFormat.ChatCompletions =>
                "{\"choices\":[{\"message\":{\"content\":" + quoted + "}}]}",
            ModelRequestFormat.Responses => "{\"output_text\":" + quoted + "}",
            ModelRequestFormat.AnthropicMessages =>
                "{\"content\":[{\"type\":\"text\",\"text\":" + quoted + "}]}",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private string LengthErrorBody() => Format switch
    {
        ModelRequestFormat.Responses => "",
        ModelRequestFormat.AnthropicMessages =>
            """{"error":{"type":"invalid_request_error","message":"prompt is too long: input exceeds maximum"}}""",
        _ => """{"error":{"code":"context_length_exceeded"}}""",
    };

    /// <summary>
    /// The custom-action fixture: a segment yields <c>{"items":[...]}</c>, and an assembly request
    /// flattens the partial documents it receives into one document.
    /// </summary>
    private static string StructuredReply(string user, string system)
    {
        var items = new List<string>();
        if (system.Contains("Result assembly contract:", StringComparison.Ordinal))
        {
            using var array = JsonDocument.Parse(user);
            foreach (var partial in array.RootElement.EnumerateArray())
            {
                using var document = JsonDocument.Parse(partial.GetString() ?? "");
                if (!document.RootElement.TryGetProperty("items", out var partialItems)) continue;
                foreach (var item in partialItems.EnumerateArray())
                {
                    items.Add(item.GetString() ?? "");
                }
            }
        }
        else
        {
            var token = user.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            items.Add(token);
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["items"] = items });
    }

    private static void Respond(HttpListenerContext context, int status, string contentType, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Flush();
        context.Response.Close();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
