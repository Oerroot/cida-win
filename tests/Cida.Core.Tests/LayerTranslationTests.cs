using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class LayerTranslationTests
{
    [Fact]
    public void UserMessageIsTheContractedJsonArray()
    {
        var message = LayerTranslationRequest.BuildUserMessage(["第一段", "second"]);
        Assert.Equal("""[{"id":0,"text":"第一段"},{"id":1,"text":"second"}]""", message);
    }

    [Fact]
    public void ReplyParsesInAnyOrderWithAllIds()
    {
        var reply = """[{"id":1,"text":"b"},{"id":0,"text":"a"}]""";
        var parsed = LayerTranslationRequest.ParseReply(reply);
        Assert.NotNull(parsed);
        Assert.Equal("a", parsed![0]);
        Assert.Equal("b", parsed[1]);
    }

    [Fact]
    public void NonArrayOrMissingFieldsReject()
    {
        Assert.Null(LayerTranslationRequest.ParseReply("\"text\""));
        Assert.Null(LayerTranslationRequest.ParseReply("""{"id":0,"text":"a"}"""));
        Assert.Null(LayerTranslationRequest.ParseReply("""[{"id":0}]"""));
        Assert.Null(LayerTranslationRequest.ParseReply("""[{"text":"a"}]"""));
    }

    [Fact]
    public void CacheEvictsOldestBeyondCapacity()
    {
        var cache = new LayerTranslationCache(capacity: 2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out var b));
        Assert.Equal("2", b);
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void RecentUseProtectsAnEntry()
    {
        var cache = new LayerTranslationCache(capacity: 2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        // Touch "a" so "b" becomes the least recently used.
        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", "3");
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void BatchesGroupByTargetLanguageAndCollectSkips()
    {
        var batches = LayerBatcher.Batches(
            ["中文", "English", "中文", "English"],
            ["简体中文", "简体中文", "简体中文", null]);
        // One translated batch (both indices) and one skipped batch.
        Assert.Equal(2, batches.Count);
        var translated = batches.First(batch => batch.Language != null);
        Assert.Equal([0, 1, 2], translated.Indices);
        var skipped = batches.First(batch => batch.Language == null);
        Assert.Equal([3], skipped.Indices);
    }

    [Fact]
    public void RoundTripThroughPromptContract()
    {
        // The request the panel layer sends carries translate_into and the JSON array.
        var paragraphs = new[] { "alpha", "beta" };
        var request = new ProcessingRequest
        {
            Text = LayerTranslationRequest.BuildUserMessage(paragraphs),
            Mode = ProcessingMode.Translate,
            MyLanguage = "简体中文",
            ForeignLanguage = "English",
            LayerTargetLanguage = "简体中文",
        };
        var prompt = ModelPromptBuilder.Build(request, new CidaSettings());
        Assert.Contains("translate_into", prompt.SystemMessage);
        Assert.Contains("""{"id":0,"text":"alpha"}""", prompt.UserMessage);
    }
}
