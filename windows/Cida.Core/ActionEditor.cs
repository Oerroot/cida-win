namespace Cida.Core;

/// <summary>
/// One complete sample result, kept per action. Ported from upstream ActionEditor.Preview
/// (#74): a result describes the prompt, the model configuration and the languages it was
/// produced with, so an edited draft or an updated configuration can tell it is out of date.
/// </summary>
public sealed record ActionPreview(
    string Prompt,
    string Fingerprint,
    (string My, string Foreign) Languages,
    string Text)
{
    /// <summary>Whether this result still describes <paramref name="action"/> under <paramref name="settings"/>.</summary>
    public bool Matches(ProcessingAction action, CidaSettings settings) =>
        Prompt == action.Prompt
        && Fingerprint == settings.ModelServiceFingerprint
        && Languages == settings.RequestLanguages();
}

/// <summary>How one sample trial ended or that it is still streaming.</summary>
public enum ActionAttemptStage
{
    Running,
    Stopped,
    Failed,
}

/// <summary>
/// One sample trial. The preview's text accumulates while the trial streams; a failed trial
/// also keeps whatever partial output arrived, and only a complete trial is promoted into
/// <see cref="ActionEditor.Previews"/>.
/// </summary>
public sealed record ActionAttempt(
    ActionPreview Preview,
    ActionAttemptStage Stage,
    bool Saved,
    ProcessingFailure? Failure);

/// <summary>
/// Settings' action drafts and their fixed-sample results, kept for the lifetime of the
/// application. Ported from upstream ActionEditor.swift (#74): browsing never requests
/// anything, only an explicit trial or applying a draft does. Saving a prompt is distinct
/// from completing its trial, so a failed request never rolls the saved policy back, and a
/// configuration failure blocks unchanged retries until the configuration or prompt changes.
/// </summary>
public sealed class ActionEditor
{
    /// <summary>The fixed sample every action trial sends; it never contains the user's text.</summary>
    public const string Sample = "This sentence need to be more clear and natural.";

    private abstract record UndoRecord
    {
        public sealed record Draft(ProcessingAction Action, ProcessingAction? Original) : UndoRecord;

        public sealed record Deletion(ProcessingAction Action, int Index) : UndoRecord;
    }

    private readonly object _gate = new();
    private readonly Func<ProcessingRequest, CidaSettings, Action<string>, CancellationToken, Task> _send;
    private readonly Func<CidaSettings, bool> _isConfigured;
    private readonly Dictionary<string, ActionPreview> _previews = new();
    private readonly Dictionary<string, ActionAttempt> _attempts = new();
    private string _selectedId = ProcessingAction.TranslationId;
    private ProcessingAction? _draft;
    private ProcessingAction? _draftOriginal;
    private string? _runningId;
    private string? _error;
    private string? _notice;
    private UndoRecord? _undo;
    private CancellationTokenSource? _cancellation;
    private int _serial;

    /// <summary>
    /// <paramref name="send"/> performs one fixed-sample request and hands every streamed
    /// piece to the editor; <paramref name="isConfigured"/> says whether a request may be
    /// sent at all. Both are injected so a settings window can use the real model service and
    /// tests can drive each trial deterministically. The default configuration check is
    /// <see cref="CidaSettings.IsModelServiceComplete"/>, matching the model client.
    /// </summary>
    public ActionEditor(
        Func<ProcessingRequest, CidaSettings, Action<string>, CancellationToken, Task> send,
        Func<CidaSettings, bool>? isConfigured = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _isConfigured = isConfigured ?? (settings => settings.IsModelServiceComplete);
    }

    /// <summary>Raised after any observable state changes, including streamed chunks.</summary>
    public event Action? Changed;

    /// <summary>The selected action's id; it starts on the protected translation action.</summary>
    public string SelectedId
    {
        get { lock (_gate) return _selectedId; }
    }

    /// <summary>The action being edited, or null while browsing saved actions.</summary>
    public ProcessingAction? Draft
    {
        get { lock (_gate) return _draft; }
    }

    /// <summary>The saved action the draft started from; null for a new action.</summary>
    public ProcessingAction? DraftOriginal
    {
        get { lock (_gate) return _draftOriginal; }
    }

    /// <summary>The last complete result per action id.</summary>
    public IReadOnlyDictionary<string, ActionPreview> Previews
    {
        get { lock (_gate) return new Dictionary<string, ActionPreview>(_previews); }
    }

    /// <summary>The current or last trial per action id; cleared when a trial completes.</summary>
    public IReadOnlyDictionary<string, ActionAttempt> Attempts
    {
        get { lock (_gate) return new Dictionary<string, ActionAttempt>(_attempts); }
    }

    /// <summary>The action whose trial is streaming, or null when nothing runs.</summary>
    public string? RunningId
    {
        get { lock (_gate) return _runningId; }
    }

    public bool IsRunning => RunningId != null;

    /// <summary>The draft's validation error, if any.</summary>
    public string? Error
    {
        get { lock (_gate) return _error; }
    }

    /// <summary>The last undoable change's notice, if any.</summary>
    public string? Notice
    {
        get { lock (_gate) return _notice; }
    }

    /// <summary>Whether the draft differs from the saved action (a new action is always dirty).</summary>
    public bool IsDirty
    {
        get { lock (_gate) return IsDirtyLocked; }
    }

    private bool IsDirtyLocked => _draft != null && !Equals(_draft, _draftOriginal);

    /// <summary>Selects another action; a dirty draft or a running trial keeps the selection.</summary>
    public void Select(string actionId)
    {
        lock (_gate)
        {
            if (IsDirtyLocked || _runningId != null) return;
            _draft = null;
            _draftOriginal = null;
            _selectedId = actionId;
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Starts editing the selected saved action.</summary>
    public void Begin(CidaSettings settings)
    {
        lock (_gate)
        {
            if (_draft != null) return;
            var action = settings.EffectiveActions.FirstOrDefault(a => a.Id == _selectedId);
            if (action == null) return;
            _draft = action;
            _draftOriginal = action;
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Replaces the draft's fields; the id never changes.</summary>
    public void UpdateDraft(Func<ProcessingAction, ProcessingAction> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (_draft == null) return;
            var updated = update(_draft);
            if (updated == null) return;
            _draft = updated with { Id = _draft.Id };
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Puts a translation or improvement draft's prompt back to the default.</summary>
    public void RestoreDefaultPrompt()
    {
        lock (_gate)
        {
            if (_draft == null || _draft.Mode == ProcessingMode.Custom) return;
            _draft = _draft with { Prompt = CidaSettings.DefaultPromptFor(_draft.Mode) };
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Starts a draft for a new custom action.</summary>
    public void Create(ref CidaSettings settings)
    {
        lock (_gate)
        {
            if (IsDirtyLocked || _runningId != null) return;
            var actions = settings.EffectiveActions;
            var number = 1;
            while (actions.Any(a => a.Name == $"新动作 {number}")) number++;
            var action = new ProcessingAction
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"新动作 {number}",
                Prompt = "",
            };
            _draft = action;
            _draftOriginal = null;
            _selectedId = action.Id;
            _error = null;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Validates and saves the draft, then runs the saved action. The save happens before the
    /// trial and is never rolled back by a failed request. A configuration failure that still
    /// matches the saved action blocks another request, including re-applying an unchanged
    /// draft.
    /// </summary>
    public bool Apply(ref CidaSettings settings)
    {
        ProcessingAction? saved = null;
        var runTrial = false;
        var savedOk = false;
        lock (_gate)
        {
            if (_draft == null) return false;
            var candidate = _draft with
            {
                Name = _draft.Name.Trim(),
                Prompt = _draft.Prompt.Trim(),
            };
            var actions = settings.EffectiveActions.ToList();
            if (candidate.Name.Length == 0)
            {
                _error = "给动作起一个名字";
            }
            else if (actions.Any(action => action.Id != candidate.Id && action.Name == candidate.Name))
            {
                _error = "已经有同名的动作了";
            }
            else if (candidate.Prompt.Length == 0)
            {
                _error = "写下你希望这个动作做什么";
            }
            else
            {
                var index = actions.FindIndex(action => action.Id == candidate.Id);
                if (index >= 0) actions[index] = candidate;
                else actions.Add(candidate);
                settings = StoreActions(settings, actions);
                _draft = null;
                _draftOriginal = null;
                _error = null;
                _notice = null;
                _undo = null;
                saved = candidate;
                savedOk = true;
                runTrial = !NeedsConfigurationLocked(candidate.Id, settings)
                    && (!PreviewMatchesLocked(candidate.Id, candidate, settings)
                        || _attempts.ContainsKey(candidate.Id));
            }
        }
        Changed?.Invoke();
        if (!savedOk || saved == null) return false;
        if (runTrial) RunPreview(saved, settings, saved: true);
        return true;
    }

    /// <summary>Drops the draft without touching the saved action or its complete preview.</summary>
    public void Discard(CidaSettings settings)
    {
        bool changed;
        lock (_gate) changed = DiscardLocked(settings);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Deletes a saved action; the protected translation action cannot be deleted.</summary>
    public void Delete(string actionId, ref CidaSettings settings)
    {
        var stopTrial = false;
        lock (_gate)
        {
            if (actionId == ProcessingAction.TranslationId)
            {
                _error = "「翻译」用于截图翻译与原处翻译，不能删除";
            }
            else if (_draft != null && _draft.Id == actionId && _draftOriginal == null)
            {
                DiscardLocked(settings);
            }
            else
            {
                var actions = settings.EffectiveActions.ToList();
                var index = actions.FindIndex(action => action.Id == actionId);
                if (index < 0) return;
                var deleted = actions[index];
                actions.RemoveAt(index);
                settings = StoreActions(settings, actions);
                _undo = new UndoRecord.Deletion(deleted, index);
                _notice = $"已删除「{deleted.Name}」";
                stopTrial = _runningId == actionId;
                _draft = null;
                _draftOriginal = null;
                _error = null;
                var remaining = settings.EffectiveActions;
                _selectedId = remaining[Math.Min(index, remaining.Count - 1)].Id;
            }
        }
        if (stopTrial) StopPreview();
        Changed?.Invoke();
    }

    /// <summary>Reorders a saved action; a dirty draft or a running trial ignores the move.</summary>
    public bool Move(string actionId, int destination, ref CidaSettings settings)
    {
        lock (_gate)
        {
            if (IsDirtyLocked || _runningId != null) return false;
            var actions = settings.EffectiveActions.ToList();
            var source = actions.FindIndex(action => action.Id == actionId);
            if (source < 0) return false;
            var moved = actions[source];
            actions.RemoveAt(source);
            actions.Insert(Math.Clamp(destination, 0, actions.Count), moved);
            settings = StoreActions(settings, actions);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Undoes the last discarded draft or deletion.</summary>
    public bool UndoChange(ref CidaSettings settings)
    {
        lock (_gate)
        {
            if (IsDirtyLocked || _undo == null) return false;
            switch (_undo)
            {
                case UndoRecord.Draft restored:
                    _draft = restored.Action;
                    _draftOriginal = restored.Original;
                    _selectedId = restored.Action.Id;
                    break;
                case UndoRecord.Deletion deletion:
                    var actions = settings.EffectiveActions.ToList();
                    if (actions.Any(action => action.Id == deletion.Action.Id)) return false;
                    actions.Insert(Math.Min(deletion.Index, actions.Count), deletion.Action);
                    settings = StoreActions(settings, actions);
                    _selectedId = deletion.Action.Id;
                    break;
            }
            _undo = null;
            _notice = null;
            _error = null;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Runs the selected saved action against the fixed sample without editing its prompt.
    /// Browsing and selecting never call this on their own.
    /// </summary>
    public void TrySavedAction(CidaSettings settings)
    {
        ProcessingAction? action;
        lock (_gate)
        {
            if (_draft != null || _runningId != null) return;
            if (NeedsConfigurationLocked(_selectedId, settings)) return;
            action = settings.EffectiveActions.FirstOrDefault(a => a.Id == _selectedId);
            if (action == null) return;
        }
        RunPreview(action, settings, saved: false);
    }

    /// <summary>
    /// Whether the last trial failed because the configuration needs attention and neither
    /// the configuration nor the saved prompt has changed since, so retrying unchanged would
    /// repeat the same failure.
    /// </summary>
    public bool NeedsConfiguration(CidaSettings settings)
    {
        lock (_gate) return NeedsConfigurationLocked(_selectedId, settings);
    }

    /// <summary>Stops a running trial, keeping its partial output and rejecting late chunks.</summary>
    public void StopPreview()
    {
        CancellationTokenSource? cancellation;
        var notify = false;
        lock (_gate)
        {
            _serial++;
            cancellation = _cancellation;
            _cancellation = null;
            var running = _runningId;
            _runningId = null;
            if (running != null)
            {
                notify = true;
                if (_attempts.TryGetValue(running, out var attempt)
                    && attempt.Stage == ActionAttemptStage.Running)
                {
                    _attempts[running] = attempt with
                    {
                        Stage = ActionAttemptStage.Stopped,
                        Failure = null,
                    };
                }
            }
        }
        if (cancellation != null)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* the trial already finished */ }
        }
        if (notify) Changed?.Invoke();
    }

    private bool NeedsConfigurationLocked(string actionId, CidaSettings settings)
    {
        if (!_attempts.TryGetValue(actionId, out var attempt)) return false;
        if (attempt.Failure?.Category != ProcessingFailureCategory.Configuration) return false;
        var action = settings.EffectiveActions.FirstOrDefault(a => a.Id == actionId);
        return action != null && attempt.Preview.Matches(action, settings);
    }

    private bool PreviewMatchesLocked(string actionId, ProcessingAction action, CidaSettings settings) =>
        _previews.TryGetValue(actionId, out var preview) && preview.Matches(action, settings);

    private bool DiscardLocked(CidaSettings settings)
    {
        if (_draft == null) return false;
        var discarded = _draft;
        if (IsDirtyLocked)
        {
            _undo = new UndoRecord.Draft(discarded, _draftOriginal);
            _notice = _draftOriginal == null
                ? $"已放弃新建「{discarded.Name}」"
                : "已放弃修改";
        }
        _draft = null;
        _draftOriginal = null;
        _error = null;
        var actions = settings.EffectiveActions;
        if (!actions.Any(action => action.Id == _selectedId))
        {
            _selectedId = actions.FirstOrDefault()?.Id ?? ProcessingAction.TranslationId;
        }
        return true;
    }

    /// <summary>Writes the action list back and keeps the canonical prompt fields in sync.</summary>
    private static CidaSettings StoreActions(CidaSettings settings, IReadOnlyList<ProcessingAction> actions)
    {
        var normalized = ProcessingAction.Normalize(actions, settings.TranslationPrompt,
            settings.ImprovementPrompt);
        var translation = normalized.FirstOrDefault(action =>
            action.Id == ProcessingAction.TranslationId);
        var improvement = normalized.FirstOrDefault(action =>
            action.Id == ProcessingAction.ImprovementId);
        return settings with
        {
            Actions = normalized,
            TranslationPrompt = translation?.Prompt ?? settings.TranslationPrompt,
            ImprovementPrompt = improvement?.Prompt ?? settings.ImprovementPrompt,
        };
    }

    /// <summary>Starts one fixed-sample trial, replacing any previous trial or attempt.</summary>
    private void RunPreview(ProcessingAction action, CidaSettings settings, bool saved)
    {
        StopPreview();
        var languages = settings.RequestLanguages();
        var preview = new ActionPreview(action.Prompt, settings.ModelServiceFingerprint,
            languages, "");
        lock (_gate)
        {
            _attempts[action.Id] = new ActionAttempt(preview, ActionAttemptStage.Running, saved, null);
        }
        Changed?.Invoke();
        if (!_isConfigured(settings))
        {
            var failure = new ProcessingFailure(ProcessingFailureCategory.Configuration,
                settings.ModelService.Model, settings.ModelServiceFingerprint);
            lock (_gate)
            {
                if (_attempts.TryGetValue(action.Id, out var attempt))
                {
                    _attempts[action.Id] = attempt with
                    {
                        Stage = ActionAttemptStage.Failed,
                        Failure = failure,
                    };
                }
            }
            Changed?.Invoke();
            return;
        }

        int serial;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            serial = ++_serial;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _runningId = action.Id;
        }
        var request = new ProcessingRequest
        {
            Text = Sample,
            Mode = action.Mode,
            ActionPrompt = action.Prompt,
            MyLanguage = languages.My,
            ForeignLanguage = languages.Foreign,
        };
        var onText = (Action<string>)(chunk => AppendChunk(action.Id, serial, chunk));
        Changed?.Invoke();
        _ = RunTrialAsync(request, settings, action.Id, serial, onText, cancellation.Token);
    }

    private void AppendChunk(string actionId, int serial, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_gate)
        {
            if (serial != _serial) return;
            if (!_attempts.TryGetValue(actionId, out var attempt)
                || attempt.Stage != ActionAttemptStage.Running) return;
            _attempts[actionId] = attempt with
            {
                Preview = attempt.Preview with { Text = attempt.Preview.Text + chunk },
            };
        }
        Changed?.Invoke();
    }

    private async Task RunTrialAsync(
        ProcessingRequest request,
        CidaSettings settings,
        string actionId,
        int serial,
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        try
        {
            await _send(request, settings, onText, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (serial != _serial) return;
                if (_attempts.TryGetValue(actionId, out var attempt))
                {
                    if (attempt.Preview.Text.Length == 0)
                    {
                        var empty = new ModelServiceException(
                            new ModelServiceErrorInfo(ModelServiceError.EmptyResult));
                        _attempts[actionId] = attempt with
                        {
                            Stage = ActionAttemptStage.Failed,
                            Failure = ProcessingFailure.From(empty, settings),
                        };
                    }
                    else
                    {
                        _previews[actionId] = attempt.Preview;
                        _attempts.Remove(actionId);
                    }
                }
                _runningId = null;
                _cancellation = null;
            }
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            CompleteStopped(actionId, serial);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (serial != _serial) return;
                if (_attempts.TryGetValue(actionId, out var attempt))
                {
                    _attempts[actionId] = attempt with
                    {
                        Stage = ActionAttemptStage.Failed,
                        Failure = ProcessingFailure.From(error, settings),
                    };
                }
                _runningId = null;
                _cancellation = null;
            }
            Changed?.Invoke();
        }
    }

    private void CompleteStopped(string actionId, int serial)
    {
        lock (_gate)
        {
            if (serial != _serial) return;
            if (_attempts.TryGetValue(actionId, out var attempt))
            {
                _attempts[actionId] = attempt with
                {
                    Stage = ActionAttemptStage.Stopped,
                    Failure = null,
                };
            }
            _runningId = null;
            _cancellation = null;
        }
        Changed?.Invoke();
    }
}
