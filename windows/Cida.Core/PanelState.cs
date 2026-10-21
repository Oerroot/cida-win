using System.Text;

namespace Cida.Core;

/// <summary>One request snapshot; UI edits never change the identity of a running result.</summary>
public sealed class PanelState
{
    private readonly StringBuilder _result = new();
    private long _serial;
    public string Source { get; set; } = "";
    public string ActionId { get; set; } = ProcessingAction.TranslationId;
    public string? SubmittedSource { get; private set; }
    public string? SubmittedActionId { get; private set; }
    public string? SubmittedConfiguration { get; private set; }
    public ResultPhase Phase { get; private set; } = ResultPhase.Completed;
    public string Result => _result.ToString();
    public string? Error { get; private set; }

    /// <summary>
    /// The safe recovery information of a failed request (#73): the panel explains the category
    /// and offers retry or model settings; provider text and the user's text stay out of it.
    /// </summary>
    public ProcessingFailure? Failure { get; private set; }
    public bool IsStale => SubmittedSource != null &&
        (Source != SubmittedSource || ActionId != SubmittedActionId);
    public string StaleReason => Source != SubmittedSource ? "原文已修改" : "动作已改变";
    public bool HasSubmission => SubmittedSource != null;
    public long Begin(string source, string actionId, string configuration)
    {
        SubmittedSource = source;
        SubmittedActionId = actionId;
        SubmittedConfiguration = configuration;
        _result.Clear(); Error = null; Failure = null; Phase = ResultPhase.Streaming;
        return ++_serial;
    }
    public bool Append(long serial, string text)
    {
        if (serial != _serial || Phase != ResultPhase.Streaming) return false;
        _result.Append(text); return true;
    }
    public bool Finish(long serial, ResultPhase phase, string? error = null)
    {
        if (serial != _serial || Phase != ResultPhase.Streaming) return false;
        Phase = phase; Error = error; return true;
    }

    /// <summary>
    /// Ends the request with a recovery failure while keeping the text already streamed: only
    /// the current streaming request can fail, exactly once, and late appends or finishes from
    /// the same or an older request are rejected.
    /// </summary>
    public bool Fail(long serial, ProcessingFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (serial != _serial || Phase != ResultPhase.Streaming) return false;
        Phase = ResultPhase.Failed; Failure = failure; return true;
    }
    public void Stop() { if (Phase == ResultPhase.Streaming) Phase = ResultPhase.Stopped; }
}
