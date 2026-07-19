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
    /// <summary>Section 7.6: assistant UI is disabled-with-explainer until a model exists.</summary>
    public const string UnavailableText =
        "No assistant model is installed. See Settings > Assistant to set one up.";

    private readonly Func<AssistantQaService?> _serviceFactory;
    private readonly AssistantChatStore _store;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly Func<string?>? _busyReason;
    private AssistantQaService? _service;

    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];
    [ObservableProperty] private string _questionText = "";
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private bool _isAvailable = true;
    /// <summary>"" idle; "Answering..." / the queued busy reason while a question runs; the
    /// unavailable explainer when no model is installed.</summary>
    [ObservableProperty] private string _statusText = "";
    /// <summary>Live streamed answer preview; cleared once the validated turn lands.</summary>
    [ObservableProperty] private string _streamingText = "";
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
        AskCommand = new AsyncRelayCommand(AskAsync, () => !IsAsking && QuestionText.Trim().Length > 0);
        NavigateChipCommand = new RelayCommand<CitationChip>(chip =>
        {
            if (chip?.SessionId is { } sid)
                CitationNavigationRequested?.Invoke(sid, chip.Seq, chip.NavTerm);
        });
    }

    partial void OnQuestionTextChanged(string value) => AskCommand.NotifyCanExecuteChanged();
    partial void OnIsAskingChanged(bool value) => AskCommand.NotifyCanExecuteChanged();

    /// <summary>Persisted history renders exactly as validated at answer time (the turns carry
    /// their AnswerLines) - self-contained, no re-validation churn on load.</summary>
    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            _dispatch(() =>
            {
                Turns.Clear();
                foreach (var t in log.Turns) Turns.Add(new ChatTurnViewModel(t));
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat history", ex); }
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
            AssistantChatTurn turn = await _service.AskAsync(question,
                new StreamProgress(this), CancellationToken.None);
            Turns.Add(new ChatTurnViewModel(turn));
            QuestionText = "";
            TurnCompleted?.Invoke(turn);
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
    /// <summary>Middle dots as the \u00B7 escape (read-view footer precedent, ASCII source).</summary>
    public string ProvenanceLine =>
        $"{Turn.Model} \u00B7 {Turn.Backend.ToUpperInvariant()} \u00B7 prompt {Turn.PromptVersion}";
}
