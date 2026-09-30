using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class ServerSentEventParserTests
{
    [Fact]
    public void EmptyLineDispatchesEvent()
    {
        var parser = new ServerSentEventParser();
        Assert.Null(parser.Consume("data: hello"));
        var @event = parser.Consume("");
        Assert.NotNull(@event);
        Assert.Equal("hello", @event!.Data);
        Assert.Null(@event.Name);
    }

    [Fact]
    public void CommentLinesAreIgnored()
    {
        var parser = new ServerSentEventParser();
        Assert.Null(parser.Consume(": ping"));
        Assert.Null(parser.Consume("data: text"));
        Assert.Equal("text", parser.Consume("")!.Data);
    }

    [Fact]
    public void MultipleDataLinesJoinWithNewlines()
    {
        var parser = new ServerSentEventParser();
        parser.Consume("event: message");
        parser.Consume("data: line1");
        parser.Consume("data: line2");
        var @event = parser.Consume("");
        Assert.Equal("message", @event!.Name);
        Assert.Equal("line1\nline2", @event.Data);
    }

    [Fact]
    public void SpaceAfterColonIsStrippedOnlyOnce()
    {
        var parser = new ServerSentEventParser();
        parser.Consume("data:  two spaces");
        Assert.Equal(" two spaces", parser.Consume("")!.Data);
    }

    [Fact]
    public void FieldWithoutColonHasEmptyValue()
    {
        var parser = new ServerSentEventParser();
        Assert.Null(parser.Consume("data"));
        Assert.Equal("", parser.Consume("")!.Data);
    }

    [Fact]
    public void FinishDispatchesOpenEvent()
    {
        var parser = new ServerSentEventParser();
        parser.Consume("data: tail");
        Assert.Equal("tail", parser.Finish()!.Data);
    }

    [Fact]
    public void UnrecognizedFieldDoesNothing()
    {
        var parser = new ServerSentEventParser();
        Assert.Null(parser.Consume("id: 42"));
        Assert.Null(parser.Consume("retry: 100"));
        Assert.Null(parser.Consume(""));
    }
}
