namespace Cida.Core;

/// <summary>
/// The translation layer's batch contract: paragraphs travel as a JSON array of
/// <c>{"id": n, "text": "..."}</c> and come back the same way. Ported from upstream
/// TranslationLayerTranslation.swift; pure logic so the pane rules stay testable.
/// </summary>
public static class LayerTranslationRequest
{
    /// <summary>Builds the user message: one JSON array entry per paragraph.</summary>
    public static string BuildUserMessage(IReadOnlyList<string> paragraphs)
    {
        var members = new List<string>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            members.Add($"{{\"id\":{index},\"text\":{JsonEscape(paragraphs[index])}}}");
        }
        return "[" + string.Join(",", members) + "]";
    }

    /// <summary>
    /// Parses the model's reply: one translation per id, every id present, in any order.
    /// Null when the reply is not the JSON array the contract asks for.
    /// </summary>
    public static IReadOnlyDictionary<int, string>? ParseReply(string reply, int? expectedCount = null)
    {
        var value = JsonValue.Parse(reply);
        if (value?.TokenType != JsonValue.Kind.Array) return null;
        var result = new Dictionary<int, string>();
        foreach (var item in value.ArrayValue!)
        {
            if (item.TokenType != JsonValue.Kind.Object) return null;
            var id = item["id"]?.IntValue;
            var text = item["text"]?.StringValue;
            if (id == null || id < 0 || string.IsNullOrWhiteSpace(text)
                || !result.TryAdd(id.Value, text)) return null;
        }
        if (expectedCount is { } count && (result.Count != count || result.Keys.Any(id => id >= count))) return null;
        return result;
    }

    private static string JsonEscape(string text) =>
        JsonValue.String(text).CompactText;

    /// <summary>
    /// The translation layer's send path (#73): one numbered-JSON request for a batch of
    /// paragraphs, with automatic recovery when the model service rejects the batch for input
    /// length. The batch is halved while it does not fit; a single paragraph is split at a
    /// lossless boundary that keeps <c>⟦n⟧</c> placeholders indivisible, and the two translations
    /// are rejoined with the source's separator. The JSON id contract never changes: each request
    /// still carries <c>id</c> 0..n-1. An explicit input-length rejection is the only failure that
    /// triggers recovery; cancellation applies to the whole sequence, including between requests.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateWithLengthRecoveryAsync(
        ModelServiceClient client,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CidaSettings settings,
        string? actionPrompt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(settings);
        if (texts.Count == 0) return [];
        var reply = new System.Text.StringBuilder();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BuildRequest(texts, targetLanguage, settings, actionPrompt);
            var prepared = ModelServiceClient.Prepare(request, settings);
            await client.SendAsync(
                prepared,
                settings.ModelService.Format,
                new SecretRedactor(settings.ApiKey),
                null,
                chunk => reply.Append(chunk),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = LayerTranslationRequest.ParseReply(reply.ToString().Trim(), texts.Count);
            if (parsed == null)
            {
                throw new ModelServiceException(
                    ModelServiceErrorInfo.UnexpectedResponse("译文与原文对不上"));
            }
            return texts.Select((_, index) => parsed[index]).ToList();
        }
        catch (Exception error) when (reply.Length == 0 && ModelServiceSegmentation.IsLengthLimit(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (texts.Count > 1)
            {
                var middle = texts.Count / 2;
                var first = await TranslateWithLengthRecoveryAsync(
                    client, texts.Take(middle).ToList(), targetLanguage, settings, actionPrompt, cancellationToken)
                    .ConfigureAwait(false);
                var second = await TranslateWithLengthRecoveryAsync(
                    client, texts.Skip(middle).ToList(), targetLanguage, settings, actionPrompt, cancellationToken)
                    .ConfigureAwait(false);
                return first.Concat(second).ToList();
            }
            // A single paragraph that is still too long: split it at a lossless boundary, keeping
            // every numbered placeholder inside one side.
            var parts = SourcePartition.Split(texts[0], preservingPlaceholders: true);
            if (parts == null) throw ModelServiceSegmentation.LengthRecoveryExhausted();
            var firstPart = await TranslateWithLengthRecoveryAsync(
                client, [parts.First], targetLanguage, settings, actionPrompt, cancellationToken)
                .ConfigureAwait(false);
            var secondPart = await TranslateWithLengthRecoveryAsync(
                client, [parts.Second], targetLanguage, settings, actionPrompt, cancellationToken)
                .ConfigureAwait(false);
            return [firstPart[0].Trim() + parts.Separator + secondPart[0].Trim()];
        }
    }

    private static ProcessingRequest BuildRequest(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CidaSettings settings,
        string? actionPrompt)
    {
        var languages = settings.RequestLanguages();
        return new ProcessingRequest
        {
            Text = LayerTranslationRequest.BuildUserMessage(texts),
            Mode = ProcessingMode.Translate,
            MyLanguage = languages.My,
            ForeignLanguage = languages.Foreign,
            LayerTargetLanguage = targetLanguage,
            ActionPrompt = actionPrompt,
        };
    }
}

/// <summary>
/// An LRU cache of paragraph translations, so a window that re-reads after scrolling
/// pays only for the paragraphs that changed. Ported from upstream LayerTranslationCache.
/// </summary>
public sealed class LayerTranslationCache(int capacity = 256)
{
    private readonly Dictionary<string, string> _entries = new();
    private readonly LinkedList<string> _order = new();
    private readonly int _capacity = capacity;

    public int Count => _entries.Count;

    public bool TryGet(string source, out string translation)
    {
        if (_entries.TryGetValue(source, out translation!))
        {
            // Move to most-recently-used.
            _order.Remove(source);
            _order.AddLast(source);
            return true;
        }
        return false;
    }

    public void Set(string source, string translation)
    {
        if (_entries.ContainsKey(source))
        {
            _entries[source] = translation;
            _order.Remove(source);
            _order.AddLast(source);
            return;
        }
        if (_order.Count >= _capacity)
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _entries.Remove(oldest);
        }
        _entries[source] = translation;
        _order.AddLast(source);
    }
}

/// <summary>
/// Batches paragraphs by language so each request holds one target language, and merges
/// the batched replies back onto paragraph indices. Ported in shape from upstream
/// <c>batches(of:)</c> and <c>LayerLanguageFilter</c>.
/// </summary>
public sealed class LayerBatcher
{
    /// <summary>
    /// Groups paragraph indices by their target language (null target skips translation:
    /// the source is already in the target language).
    /// </summary>
    public static IReadOnlyList<(string? Language, IReadOnlyList<int> Indices)> Batches(
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<string?> targets)
    {
        var byLanguage = new Dictionary<string, List<int>>();
        var skipped = new List<int>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            var target = index < targets.Count ? targets[index] : null;
            if (target == null)
            {
                skipped.Add(index);
                continue;
            }
            if (!byLanguage.TryGetValue(target, out var indices))
            {
                indices = [];
                byLanguage[target] = indices;
            }
            indices.Add(index);
        }
        var result = new List<(string?, IReadOnlyList<int>)>();
        foreach (var (language, indices) in byLanguage)
        {
            result.Add((language, indices));
        }
        if (skipped.Count > 0)
        {
            result.Add((null, skipped));
        }
        return result;
    }
}
