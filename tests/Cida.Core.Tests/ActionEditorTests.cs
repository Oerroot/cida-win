using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

/// <summary>
/// Port of upstream ActionEditorTests.swift (#74). Every request is controlled by a
/// TaskCompletionSource-style fake, so a trial only completes when the test says so; polling
/// has a bounded wait so a wrong state fails the test instead of hanging the runner.
/// </summary>
public sealed class ActionEditorTests
{
    private const string TranslationId = ProcessingAction.TranslationId;
    private const string ImprovementId = ProcessingAction.ImprovementId;

    [Fact]
    public async Task BrowsingAndDraftEditingNeverRequestAndDirtyDraftCannotRun()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Select(TranslationId);
        editor.Select(ImprovementId);
        editor.Select(TranslationId);
        Assert.Equal(0, send.Count);

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Draft policy" });
        Assert.True(editor.IsDirty);
        editor.Select(ImprovementId);
        Assert.Equal(TranslationId, editor.SelectedId);
        editor.TrySavedAction(settings);
        Assert.Equal(0, send.Count);
        editor.Discard(settings);
        Assert.Equal(0, send.Count);

        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 1, "an explicit trial after discarding sends one request");
        send.Finish(0, "Result");
        await WaitFor(() => editor.RunningId == null, "the trial completes");
        Assert.Equal("Result", Preview(editor, TranslationId)!.Text);
    }

    [Fact]
    public async Task SavedActionTrialUsesTheFixedSampleAndStopKeepsOnlyTheCurrentPartialOutput()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Select(ImprovementId);
        Assert.Equal(0, send.Count);

        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 1, "the explicit trial sends one request");
        Assert.Null(editor.Draft);
        Assert.Equal(ActionEditor.Sample, send.Requests[0].Text);
        Assert.Equal(ProcessingMode.Improve, send.Requests[0].Mode);
        send.Finish(0, "Previous complete result");
        await WaitFor(() => editor.RunningId == null, "the first trial completes");
        Assert.Equal("Previous complete result", Preview(editor, ImprovementId)!.Text);

        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 2, "the second trial starts");
        send.Yield(1, "Current partial");
        await WaitFor(
            () => Attempt(editor, ImprovementId)?.Preview.Text == "Current partial",
            "the partial output streams into the attempt");
        Assert.Equal("Previous complete result", Preview(editor, ImprovementId)!.Text);

        editor.StopPreview();
        send.Finish(1, " must not appear");
        await Task.Yield();
        var stopped = Attempt(editor, ImprovementId);
        Assert.NotNull(stopped);
        Assert.Equal("Current partial", stopped!.Preview.Text);
        Assert.Equal(ActionAttemptStage.Stopped, stopped.Stage);
        Assert.Equal("Previous complete result", Preview(editor, ImprovementId)!.Text);

        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 3, "a stopped trial may run again");
        send.Finish(2, "New complete result");
        await WaitFor(() => editor.RunningId == null, "the retried trial completes");
        Assert.Null(Attempt(editor, ImprovementId));
        Assert.Equal("New complete result", Preview(editor, ImprovementId)!.Text);
    }

    [Fact]
    public async Task ApplySavesBeforeTrialAndConfigurationFailureDoesNotRollBack()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Keep the new policy" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 1, "apply runs the saved action");
        Assert.Equal("Keep the new policy", settings.TranslationPrompt);

        send.Fail(0, new ModelServiceException(ModelServiceErrorInfo.Http(401,
            "PRIVATE_PROVIDER_DETAIL", "PRIVATE_PROVIDER_DETAIL")));
        await WaitFor(() => editor.RunningId == null, "the failed trial finishes");
        Assert.Equal("Keep the new policy", settings.TranslationPrompt);

        var failed = Attempt(editor, TranslationId);
        Assert.NotNull(failed);
        Assert.True(failed!.Saved);
        Assert.NotNull(failed.Failure);
        Assert.Equal(ProcessingFailureCategory.Configuration, failed.Failure!.Category);
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", failed.Failure.Explanation);
        Assert.True(editor.NeedsConfiguration(settings));

        // Neither an explicit trial nor finishing an unchanged draft may repeat the failure.
        editor.TrySavedAction(settings);
        Assert.Equal(1, send.Count);
        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        Assert.Equal(1, send.Count);
        Assert.True(editor.NeedsConfiguration(settings));

        // A configuration change allows another trial, which is a trial rather than a save.
        settings = settings with
        {
            ModelService = settings.ModelService with { Model = "another-model" },
        };
        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 2, "a changed configuration allows another trial");
        var retried = Attempt(editor, TranslationId);
        Assert.NotNull(retried);
        Assert.False(retried!.Saved);
        send.Finish(1, "Recovered");
        await WaitFor(() => editor.RunningId == null, "the retried trial completes");
        Assert.False(editor.NeedsConfiguration(settings));
        Assert.Equal("Recovered", Preview(editor, TranslationId)!.Text);
    }

    [Fact]
    public async Task ConfigurationFailureAllowsRetryOnceTheSavedPromptChanges()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Policy one" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 1, "the first trial starts");
        send.Fail(0, new ModelServiceException(ModelServiceErrorInfo.Http(403,
            "PRIVATE_PROVIDER_DETAIL", "PRIVATE_PROVIDER_DETAIL")));
        await WaitFor(() => editor.RunningId == null, "the failure is recorded");
        Assert.True(editor.NeedsConfiguration(settings));

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Policy two" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 2, "a changed saved prompt allows another trial");
        Assert.Equal("Policy two", settings.TranslationPrompt);
        Assert.False(editor.NeedsConfiguration(settings));
        send.Finish(1, "Recovered");
        await WaitFor(() => editor.RunningId == null, "the retried trial completes");
        Assert.Equal("Recovered", Preview(editor, TranslationId)!.Text);
    }

    [Fact]
    public async Task DiscardLeavesSavedPolicyAndPreviewUntouched()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.TrySavedAction(settings);
        await WaitFor(() => send.Count == 1, "the reference trial starts");
        send.Finish(0, "Reference");
        await WaitFor(() => editor.RunningId == null, "the reference trial completes");

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Unsaved policy" });
        editor.TrySavedAction(settings);
        Assert.Equal(1, send.Count);
        editor.Discard(settings);
        Assert.Null(editor.Draft);
        Assert.Equal("已放弃修改", editor.Notice);
        var preview = Preview(editor, TranslationId);
        Assert.NotNull(preview);
        Assert.Equal(settings.TranslationPrompt, preview!.Prompt);
        Assert.Equal("Reference", preview.Text);
    }

    [Fact]
    public async Task ApplySkipsUnchangedResultsAndSupersededRequestsCannotReplaceThem()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Select(ImprovementId);
        editor.Begin(settings);
        Assert.Equal(0, send.Count);
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 1, "the first apply starts a trial");
        Assert.Equal(ActionEditor.Sample, send.Requests[0].Text);
        send.Finish(0, "First result");
        await WaitFor(() => editor.RunningId == null, "the first trial completes");
        Assert.Equal("First result", Preview(editor, ImprovementId)!.Text);

        // Renaming a previewed action sends no request.
        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Name = "润色" });
        Assert.True(editor.Apply(ref settings));
        Assert.Equal(1, send.Count);

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Shorter" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 2, "a changed prompt starts a trial");
        Assert.Equal("First result", Preview(editor, ImprovementId)!.Text);

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Shortest" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 3, "the superseded trial is replaced");
        send.Finish(2, "Latest");
        await WaitFor(() => editor.RunningId == null, "the latest trial completes");
        send.Finish(1, "Obsolete");
        await Task.Yield();
        Assert.Equal("Latest", Preview(editor, ImprovementId)!.Text);

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "Stop me" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 4, "the stopped trial starts");
        editor.StopPreview();
        send.Finish(3, "Stopped");
        await Task.Yield();
        Assert.Equal("Latest", Preview(editor, ImprovementId)!.Text);
        Assert.Null(editor.RunningId);
        Assert.Equal(ActionAttemptStage.Stopped, Attempt(editor, ImprovementId)!.Stage);
    }

    [Fact]
    public async Task FailedPreviewKeepsThePreviousOutputAndCanBeRetried()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 1, "the first trial starts");
        send.Finish(0, "Previous");
        await WaitFor(() => editor.RunningId == null, "the first trial completes");

        editor.Begin(settings);
        editor.UpdateDraft(action => action with { Prompt = "A changed policy" });
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 2, "the changed prompt starts a trial");
        send.Fail(1, new ModelServiceException(
            new ModelServiceErrorInfo(ModelServiceError.EmptyResult)));
        await WaitFor(() => editor.RunningId == null, "the failed trial finishes");
        Assert.Equal("Previous", Preview(editor, TranslationId)!.Text);
        Assert.NotNull(Attempt(editor, TranslationId)?.Failure);
        Assert.False(editor.NeedsConfiguration(settings));

        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 3, "a non-configuration failure may retry unchanged");
        send.Finish(2, "Recovered");
        await WaitFor(() => editor.RunningId == null, "the retried trial completes");
        Assert.Equal("Recovered", Preview(editor, TranslationId)!.Text);
        Assert.Null(Attempt(editor, TranslationId)?.Failure);
    }

    [Fact]
    public void UnconfiguredServiceSavesTheActionAndDefaultRestorationRequiresApply()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings { TranslationPrompt = "Custom translation policy" };
        var editor = new ActionEditor(send.Handler);
        editor.Begin(settings);
        editor.RestoreDefaultPrompt();
        Assert.Equal("Custom translation policy", settings.TranslationPrompt);
        Assert.Equal(CidaSettings.DefaultTranslationPrompt, editor.Draft!.Prompt);

        Assert.True(editor.Apply(ref settings));
        Assert.Equal(CidaSettings.DefaultTranslationPrompt, settings.TranslationPrompt);
        Assert.Null(editor.RunningId);
        Assert.Equal(0, send.Count);
        var attempt = Attempt(editor, TranslationId);
        Assert.NotNull(attempt);
        Assert.Equal(ActionAttemptStage.Failed, attempt!.Stage);
        Assert.Equal(ProcessingFailureCategory.Configuration, attempt.Failure!.Category);
        Assert.Null(Preview(editor, TranslationId));
        Assert.True(editor.NeedsConfiguration(settings));
    }

    [Fact]
    public async Task DeletingARunningActionRejectsLateOutputAndUndoCanRetry()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Select(ImprovementId);
        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 1, "the trial starts");
        editor.Delete(ImprovementId, ref settings);
        Assert.Null(editor.RunningId);
        send.Finish(0, "Deleted result");
        await Task.Yield();
        Assert.Null(Preview(editor, ImprovementId));
        Assert.DoesNotContain(settings.EffectiveActions,
            action => action.Id == ImprovementId);

        Assert.True(editor.UndoChange(ref settings));
        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        await WaitFor(() => send.Count == 2, "the restored action can be tried again");
        send.Finish(1, "Restored result");
        await WaitFor(() => editor.RunningId == null, "the restored trial completes");
        Assert.Equal("Restored result", Preview(editor, ImprovementId)!.Text);
    }

    [Fact]
    public async Task BlankDraftsCannotSaveAndValidFieldsAreTrimmed()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        var original = settings.EffectiveActions;
        editor.Create(ref settings);
        var invalid = new (string Name, string Prompt, string Error)[]
        {
            (" \n", "Summarize", "给动作起一个名字"),
            ("Summary", " \n", "写下你希望这个动作做什么"),
            (" 翻译 ", "Summarize", "已经有同名的动作了"),
        };
        foreach (var (name, prompt, message) in invalid)
        {
            editor.UpdateDraft(action => action with { Name = name, Prompt = prompt });
            Assert.False(editor.Apply(ref settings));
            Assert.Equal(message, editor.Error);
            Assert.Equal(original, settings.EffectiveActions);
            Assert.Equal(0, send.Count);
        }

        editor.UpdateDraft(action => action with { Name = " Summary \n", Prompt = " Summarize \n" });
        Assert.True(editor.Apply(ref settings));
        Assert.Equal("Summary", settings.EffectiveActions.Last().Name);
        Assert.Equal("Summarize", settings.EffectiveActions.Last().Prompt);
        await WaitFor(() => send.Count == 1, "the saved action is tried");
        editor.StopPreview();
    }

    [Fact]
    public async Task ModelAndLanguageChangesInvalidateTheCachedPreviewOnApply()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        for (var index = 0; index < 3; index++)
        {
            if (index == 1)
            {
                settings = settings with
                {
                    ModelService = settings.ModelService with { Model = "another-model" },
                };
            }
            if (index == 2) settings = settings with { ForeignLanguage = "French" };
            editor.Begin(settings);
            Assert.True(editor.Apply(ref settings));
            await WaitFor(() => send.Count == index + 1, "changed settings start a trial");
            send.Finish(index, $"Result {index}");
            await WaitFor(() => editor.RunningId == null, "the trial completes");
            var preview = Preview(editor, TranslationId);
            Assert.NotNull(preview);
            Assert.Equal(settings.ModelServiceFingerprint, preview!.Fingerprint);
            Assert.Equal(settings.RequestLanguages().Foreign, preview.Languages.Foreign);
        }
        Assert.Equal("French", send.Requests[2].ForeignLanguage);

        // No change: the latest complete preview is reused without a request.
        editor.Begin(settings);
        Assert.True(editor.Apply(ref settings));
        Assert.Equal(3, send.Count);
    }

    [Fact]
    public async Task DraftValidationDiscardUndoAndProtectedTranslationFollowUpstream()
    {
        var send = new ControlledSend();
        var settings = new CidaSettings();
        var editor = new ActionEditor(send.Handler, _ => true);
        editor.Create(ref settings);
        var id = editor.SelectedId;
        editor.UpdateDraft(action => action with { Name = "翻译", Prompt = "Summarize" });
        Assert.False(editor.Apply(ref settings));
        Assert.Equal("已经有同名的动作了", editor.Error);

        // A dirty draft keeps its selection.
        editor.Select(TranslationId);
        Assert.Equal(id, editor.SelectedId);
        editor.UpdateDraft(action => action with { Name = "摘要" });
        editor.Discard(settings);
        Assert.Null(editor.Draft);
        Assert.Equal(2, settings.EffectiveActions.Count);

        Assert.True(editor.UndoChange(ref settings));
        Assert.Equal("摘要", editor.Draft?.Name);
        Assert.True(editor.Apply(ref settings));
        Assert.Equal(id, settings.EffectiveActions.Last().Id);
        await WaitFor(() => send.Count == 1, "applying the new action starts a trial");
        editor.StopPreview();

        Assert.True(editor.Move(id, 0, ref settings));
        Assert.Equal(id, settings.EffectiveActions[0].Id);
        editor.Delete(id, ref settings);
        Assert.True(editor.UndoChange(ref settings));
        Assert.Equal(id, settings.EffectiveActions[0].Id);

        editor.Delete(TranslationId, ref settings);
        Assert.Contains(settings.EffectiveActions, action => action.Id == TranslationId);
        Assert.NotNull(editor.Error);
        editor.StopPreview();
    }

    private static ActionPreview? Preview(ActionEditor editor, string actionId) =>
        editor.Previews.TryGetValue(actionId, out var preview) ? preview : null;

    private static ActionAttempt? Attempt(ActionEditor editor, string actionId) =>
        editor.Attempts.TryGetValue(actionId, out var attempt) ? attempt : null;

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(5);
        }
        Assert.True(condition(), because);
    }

    /// <summary>Accepts requests and keeps each stream open until the test releases it.</summary>
    private sealed class ControlledSend
    {
        private readonly object _gate = new();
        private readonly List<Call> _calls = new();

        public ControlledSend()
        {
            Handler = (request, _, onText, token) =>
            {
                var call = new Call(request);
                lock (_gate) _calls.Add(call);
                return call.RunAsync(onText, token);
            };
        }

        public Func<ProcessingRequest, CidaSettings, Action<string>, CancellationToken, Task> Handler { get; }

        public int Count
        {
            get { lock (_gate) return _calls.Count; }
        }

        public IReadOnlyList<ProcessingRequest> Requests
        {
            get { lock (_gate) return _calls.Select(call => call.Request).ToList(); }
        }

        public void Yield(int index, string text) => CallAt(index).Push(text);

        public void Finish(int index, string text) => CallAt(index).Finish(text);

        public void Fail(int index, Exception error) => CallAt(index).Fail(error);

        private Call CallAt(int index)
        {
            lock (_gate) return _calls[index];
        }

        private readonly record struct StreamEvent(string Text, Exception? Error, bool Done);

        private sealed class Call
        {
            private readonly object _gate = new();
            private readonly Queue<StreamEvent> _events = new();
            private TaskCompletionSource? _waiter;

            public Call(ProcessingRequest request) => Request = request;

            public ProcessingRequest Request { get; }

            public void Push(string text) => Enqueue(new StreamEvent(text, null, false));

            public void Finish(string text) => Enqueue(new StreamEvent(text, null, true));

            public void Fail(Exception error) => Enqueue(new StreamEvent("", error, true));

            private void Enqueue(StreamEvent item)
            {
                lock (_gate)
                {
                    _events.Enqueue(item);
                    var waiter = _waiter;
                    _waiter = null;
                    waiter?.TrySetResult();
                }
            }

            public async Task RunAsync(Action<string> onText, CancellationToken token)
            {
                while (true)
                {
                    StreamEvent item;
                    Task? wait = null;
                    lock (_gate)
                    {
                        if (_events.Count > 0)
                        {
                            item = _events.Dequeue();
                        }
                        else
                        {
                            _waiter ??= new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            wait = _waiter.Task;
                            item = default;
                        }
                    }
                    if (wait != null)
                    {
                        await wait.WaitAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        continue;
                    }
                    if (item.Error != null) throw item.Error;
                    token.ThrowIfCancellationRequested();
                    if (item.Text.Length > 0) onText(item.Text);
                    if (item.Done) return;
                }
            }
        }
    }
}
