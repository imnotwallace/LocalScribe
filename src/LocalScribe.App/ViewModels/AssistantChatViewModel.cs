using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>Scope-agnostic assistant chat (design 2026-07-18 sections 7.5-7.7): the Session
/// Details Assistant tab and the Matters Assistant tab both bind this VM over their own
/// AssistantQaService. Multi-turn UI, single-turn to the model (v1 recorded constraint - the
/// service's warm session skips the re-prefill). Failures surface via the reporter and add
/// NOTHING; the AI-draft label rides every rendered turn (locked rule).</summary>
public sealed partial class AssistantChatViewModel : ObservableObject
{
    /// <summary>LOCKED (design section 1): every rendered assistant artifact carries this.
    /// Aliased to the foundation's own constant (branch 6, merged) rather than a separate
    /// literal - a single source of truth so the VM label can never drift from the Core prompt
    /// label (review finding: two independently-typed copies of a locked evidentiary string is
    /// a silent-drift risk).</summary>
    public const string AiDraftLabel = AssistantPrompts.DraftLabel;
    /// <summary>Section 7.6 + 2026-07-23 section 4: assistant chat is disabled until BOTH a
    /// model and the deployed helper exist; Settings > Assistant names which one is missing.</summary>
    public const string UnavailableText =
        "The assistant is not available - see Settings > Assistant for model and helper status.";

    private readonly Func<AssistantQaService?> _serviceFactory;
    private readonly AssistantChatStore _store;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly Func<string?>? _busyReason;
    private AssistantQaService? _service;
    /// <summary>The thread this panel's asks target (design 2026-07-24, Task 4). Captured once
    /// at load time (the first non-archived thread) and stays pinned to it across asks - a
    /// re-resolve on every ask would jump threads if something else (Phase 2's selector) ever
    /// mints one ahead of it. Null only before the first LoadHistoryAsync/ask on a brand-new
    /// store; the service mints "Chat 1" for a null id and this VM then adopts whatever it
    /// created so the next ask targets the SAME thread instead of minting another.</summary>
    private string? _activeThreadId;

    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];
    [ObservableProperty] private string _questionText = "";
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private bool _isAvailable = true;
    /// <summary>"" idle; "Answering..." / the queued busy reason while a question runs; the
    /// unavailable explainer when no model is installed.</summary>
    [ObservableProperty] private string _statusText = "";
    /// <summary>Live streamed answer preview; cleared once the validated turn lands.</summary>
    [ObservableProperty] private string _streamingText = "";
    /// <summary>True while an ARCHIVED thread is selected (addendum 2026-07-25): archived threads
    /// are read-only until unarchived, so the Ask gate must refuse - archiving is "hide, keep on
    /// disk", and appending to a hidden-by-default thread would silently grow evidence the user
    /// believes is closed.</summary>
    [ObservableProperty] private bool _isReadOnly;
    public IAsyncRelayCommand AskCommand { get; }
    public IRelayCommand<CitationChip> NavigateChipCommand { get; }
    /// <summary>(sessionId, seq, navTerm) - the exact triple the search-page snippet
    /// click-through uses; seq &lt; 0 opens the read view without scrolling.</summary>
    public event Action<string, int, string>? CitationNavigationRequested;
    /// <summary>Raised after a successful turn (the matter surface refreshes its coverage
    /// disclosure from the turn's included/omitted/missing lists).</summary>
    public event Action<AssistantChatTurn>? TurnCompleted;

    public AssistantChatViewModel(Func<AssistantQaService?> serviceFactory, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)
    {
        (_serviceFactory, _store, _reporter, _dispatch, _busyReason)
            = (serviceFactory, store, reporter, dispatch, busyReason);
        AskCommand = new AsyncRelayCommand(AskAsync,
            () => !IsAsking && !IsReadOnly && QuestionText.Trim().Length > 0);
        NavigateChipCommand = new RelayCommand<CitationChip>(chip =>
        {
            if (chip?.SessionId is { } sid)
                CitationNavigationRequested?.Invoke(sid, chip.Seq, chip.NavTerm);
        });
    }

    partial void OnQuestionTextChanged(string value) => AskCommand.NotifyCanExecuteChanged();
    partial void OnIsAskingChanged(bool value) => AskCommand.NotifyCanExecuteChanged();
    partial void OnIsReadOnlyChanged(bool value) => AskCommand.NotifyCanExecuteChanged();

    /// <summary>Persisted history renders exactly as validated at answer time (the turns carry
    /// their AnswerLines) - self-contained, no re-validation churn on load. The active thread is
    /// the first non-archived thread (design 2026-07-24 Decision 2) - there is no thread selector
    /// yet (Phase 2), so this panel always shows and appends to that one thread; its id is
    /// captured so AskAsync stays pinned to it rather than re-resolving every time.</summary>
    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            var active = log.Chats.FirstOrDefault(c => !c.Archived);
            _activeThreadId = active?.Id;
            _dispatch(() =>
            {
                Turns.Clear();
                foreach (var t in active?.Turns ?? []) Turns.Add(new ChatTurnViewModel(t));
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat history", ex); }
    }

    /// <summary>Thread switch (Phase 2 selector): swap the RENDERED turn list to the given thread
    /// and pin future asks to it. Deliberately never touches _service - the warm helper's KV
    /// prefix is the scope context, shared by every thread of this scope (design "Architecture >
    /// One warm helper"), so switching threads must never reload the transcript. An unknown id
    /// (thread deleted/renamed underneath a stale selector item) keeps the current view.</summary>
    public async Task SelectThreadAsync(string threadId, CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            var thread = log.Chats.FirstOrDefault(c => c.Id == threadId);
            if (thread is null) return;
            _activeThreadId = thread.Id;
            _dispatch(() =>
            {
                Turns.Clear();
                foreach (var t in thread.Turns) Turns.Add(new ChatTurnViewModel(t));
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat thread", ex); }
    }

    /// <summary>Context changed (correction save, split, re-transcription, tag change): tear the
    /// warm helper down so the next question re-prefills against the CURRENT record (the
    /// section 7.1 staleness rule). The service also self-detects payload drift - this just
    /// releases the helper promptly.</summary>
    public void InvalidateContext()
    {
        var s = Interlocked.Exchange(ref _service, null);
        if (s is not null) _ = s.DisposeAsync();
    }

    /// <summary>Chat close / scope change teardown (design 7.1).</summary>
    public void Shutdown() => InvalidateContext();

    /// <summary>Reverse direction of "one heavy engine at a time" (design 7.1): a recording START
    /// preempts an in-flight chat answer so the assistant yields the engine to live
    /// transcription. Forwards to the underlying service (a no-op if none is warmed / none is
    /// asking).</summary>
    public void CancelForRecording() => _service?.CancelForRecording();

    private async Task AskAsync()
    {
        string question = QuestionText.Trim();
        if (question.Length == 0) return;
        _service ??= _serviceFactory();
        if (_service is null)
        {
            IsAvailable = false;
            StatusText = UnavailableText;
            return;
        }
        IsAvailable = true;
        IsAsking = true;
        StatusText = _busyReason?.Invoke() ?? "Answering...";
        StreamingText = "";
        try
        {
            AssistantChatTurn turn = await _service.AskAsync(question, _activeThreadId,
                new StreamProgress(this), CancellationToken.None);
            // Render/clear/notify the turn that landed FIRST - it is already persisted; a later
            // bookkeeping failure below must never turn a genuinely successful, on-disk turn into
            // a reported failure (the locked "on failure nothing renders/persists" posture cuts
            // the other way here: on SUCCESS it must always render, or a retry would silently
            // double-persist a duplicate turn the user never saw).
            Turns.Add(new ChatTurnViewModel(turn));
            QuestionText = "";
            TurnCompleted?.Invoke(turn);
            if (_activeThreadId is null)
            {
                // Best-effort: on a brand-new store the service just minted "Chat 1" itself
                // (ResolveThread) - adopt its id so the NEXT ask targets that same thread instead
                // of the service minting (and this VM then persisting into) a second one. Isolated
                // in its own try/catch: if this reload fails (transient file contention, AV lock),
                // leave _activeThreadId null - the next ask simply re-resolves the first
                // non-archived thread (the same one), and the turn that already landed above is
                // unaffected either way.
                try
                {
                    _activeThreadId = (await _store.LoadAsync(CancellationToken.None))
                        .Chats.FirstOrDefault(c => !c.Archived)?.Id;
                }
                catch { /* best-effort only; never fail a turn that already rendered/persisted. */ }
            }
        }
        catch (OperationCanceledException)
        {
            // A recording started and preempted this answer (design 7.1). Nothing was persisted; the question is kept.
        }
        catch (Exception ex)
        {
            // Design 7.7: visible error, nothing persisted, nothing rendered; the question text
            // is deliberately kept so the user can retry.
            _reporter.Report("Assistant answer", ex);
        }
        finally
        {
            IsAsking = false;
            StatusText = "";
            StreamingText = "";
        }
    }

    private sealed class StreamProgress(AssistantChatViewModel vm) : IProgress<string>
    {
        public void Report(string value) => vm._dispatch(() => vm.StreamingText += value);
    }
}

/// <summary>Display projection of one persisted turn: question, validated lines (chips +
/// verdicts exactly as at answer time), the coverage disclosure, the AI-draft label and the
/// model-backend-prompt provenance line (middle dot escape - read-view footer precedent).</summary>
public sealed class ChatTurnViewModel
{
    public ChatTurnViewModel(AssistantChatTurn turn) => Turn = turn;

    public AssistantChatTurn Turn { get; }
    public string Question => Turn.Question;
    public IReadOnlyList<AnswerLine> Lines => Turn.Lines;
    public string? Disclosure => Turn.Disclosure;
    public int UnverifiableClaims => Turn.UnverifiableClaims;
    public string AiLabel => AssistantChatViewModel.AiDraftLabel;
    /// <summary>Middle dots as the \u00B7 escape (read-view footer precedent, ASCII source). The
    /// CUDA-fell clause uses the EXACT wording of the summary line (AssistantTabViewModel) so a
    /// degraded chat answer read from history is never silently labelled plain "CPU".</summary>
    public string ProvenanceLine =>
        $"{Turn.Model} \u00B7 {Turn.Backend.ToUpperInvariant()}{(Turn.CudaFellToCpu ? " - GPU unavailable, fell to CPU" : "")} \u00B7 prompt {Turn.PromptVersion}";
}
