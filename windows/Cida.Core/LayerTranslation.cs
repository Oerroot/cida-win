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
    public static IReadOnlyDictionary<int, string>? ParseReply(string reply)
    {
        var value = JsonValue.Parse(reply);
        if (value?.TokenType != JsonValue.Kind.Array) return null;
        var result = new Dictionary<int, string>();
        foreach (var item in value.ArrayValue!)
        {
            if (item.TokenType != JsonValue.Kind.Object) return null;
            var id = item["id"]?.IntValue;
            var text = item["text"]?.StringValue;
            if (id == null || text == null) return null;
            result[id.Value] = text;
        }
        return result;
    }

    private static string JsonEscape(string text) =>
        JsonValue.String(text).CompactText;
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
