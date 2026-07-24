using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>Q&amp;A orchestration over the foundation warm-helper contract (design 2026-07-18
/// sections 7.1 + 7.5 + 7.7; threading + condense added 2026-07-24). One instance per open chat
/// scope. The warm session is REUSED while the warmup payload is byte-identical (KV reuse -
/// follow-up questions skip the re-prefill) and rebuilt when the context changes; the engine
/// lease (production: the foundation AssistantGate - queued while a recording runs) wraps every
/// model call, INCLUDING any condense folds that ask requires - a no-condense ask still shows
/// exactly one acquire/release pair. A turn is persisted ONLY after a successful AssistantDone -
/// errors, truncated streams and empty answers persist NOTHING and reset the session. A
/// single-flight semaphore (Task-6 reviewer + branch-6 note N3) serializes overlapping AskAsync
/// calls - the store is an unlocked read-modify-write and the warm-session fields are mutable,
/// so two concurrent asks on one service must never interleave. DisposeAsync = teardown on chat
/// close / scope change; the 5-minute idle teardown is the foundation session's own duty.</summary>
public sealed class AssistantQaService : IAsyncDisposable
{
    private readonly IAssistantChatSessionFactory _factory;
    private readonly AssistantChatStore _store;
    private readonly Func<CancellationToken, Task<IAsyncDisposable>> _acquireEngineLease;
    private readonly Func<string, CancellationToken, Task<QaScope>> _scopeFor;
    private readonly TimeProvider _time;
    /// <summary>Test seam (Task 3): overrides the fits-gate budget used by the condense policy so
    /// a small transcript can be made to overflow deterministically without a real 32k transcript.
    /// Production always uses the default (the real 2026-07-18 operating budget).</summary>
    private readonly int _fitsBudgetTokens;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private IAssistantChatSession? _session;
    private string? _warmPayload;
    private readonly object _cancelLock = new();
    private CancellationTokenSource? _activeAskCts;

    public AssistantQaService(IAssistantChatSessionFactory factory, AssistantChatStore store,
        Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease,
        Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time,
        int fitsBudgetTokens = TokenBudget.MaxCtxTokens)
        => (_factory, _store, _acquireEngineLease, _scopeFor, _time, _fitsBudgetTokens)
            = (factory, store, acquireEngineLease, scopeFor, time, fitsBudgetTokens);

    /// <summary>TRANSITIONAL (Task 3): defaults to the active thread so the App VM
    /// (AssistantChatViewModel) and every pre-threading test keep compiling and passing
    /// unchanged. Removed in Task 4 when the VM is rewired to pass the real thread id.</summary>
    public Task<AssistantChatTurn> AskAsync(string question, IProgress<string>? chunks, CancellationToken ct)
        => AskAsync(question, threadId: null, chunks, ct);

    /// <summary>Threaded ask (design 2026-07-24): resolves the target thread (by id, else the
    /// first non-archived thread, else a freshly minted "Chat 1"), runs the budget-driven
    /// condense-to-recap policy under the SAME engine lease as the answer, then builds the answer
    /// prompt with that thread's history and appends the turn to it.</summary>
    public async Task<AssistantChatTurn> AskAsync(string question, string? threadId,
        IProgress<string>? chunks, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        // Reverse direction of "one heavy engine at a time" (design 7.1): publish a linked CTS
        // for THIS running ask only AFTER the single-flight guard is acquired, so an ask still
        // queued behind another (not yet past _oneAtATime) never owns _activeAskCts - only the
        // one ask that is actually running the engine does. The semaphore serializes execution,
        // so at most one ask is ever running past this point; a single field is therefore safe.
        using var askCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_cancelLock) { _activeAskCts = askCts; }
        var askCt = askCts.Token;
        try
        {
            QaScope scope = await _scopeFor(question, askCt);
            if (scope.NoMatches)
                throw new InvalidOperationException(
                    "There is nothing to answer from in this scope yet (no matching excerpts, or no session summaries generated).");

            AssistantChatLog log = await _store.LoadAsync(askCt);
            AssistantChatThread thread = ResolveThread(log, threadId);

            string answer;
            string backend;
            bool cudaFell;
            string historyBlock;
            try
            {
                // The lease wraps the WHOLE model-interaction block - the condense loop below
                // (zero or more one-shot folds) plus the answer ask - so a no-condense ask still
                // shows exactly one acquire/release pair (locked by
                // Ask_streams_chunks_validates_citations_and_persists_the_turn's order assertion).
                await using IAsyncDisposable lease = await _acquireEngineLease(askCt);

                (thread, log, historyBlock) = await CondenseIfOverflowingAsync(scope, thread, log, question, askCt);

                if (_session is null
                    || !string.Equals(_warmPayload, scope.WarmupRequest.PayloadJson, StringComparison.Ordinal))
                {
                    await ResetSessionAsync();
                    _session = await _factory.StartAsync(scope.WarmupRequest, askCt);
                    _warmPayload = scope.WarmupRequest.PayloadJson;
                }
                // Read AFTER the ensure block so a freshly rebuilt session's load-time verdict is
                // used. backend=cpu alone cannot tell a fall from a requested-CPU run (design 5) -
                // the fall fires during warmup (inside the factory) and rides on the session.
                cudaFell = _session.CudaFellToCpu;
                var sb = new StringBuilder();
                AssistantDone? done = null;
                // Contract resolution #4: send the FULL prompt every ask, byte-identical up to
                // the history+question tail, so the helper's KV prefix (prefilled by the warmup,
                // which always uses an empty history block) is reused - only the tail is new
                // prefill. historyBlock is "" for a first question, so the tail (and hence the
                // whole prompt) is byte-identical to the pre-threading v1 shape.
                string payload = AssistantWire.PromptPayload(
                    AssistantPrompts.BuildAnswerPrompt(scope.SpeakerPreamble, scope.ContextText, historyBlock, question),
                    QaScopeFactory.MaxAnswerTokens);
                await foreach (AssistantEvent ev in _session.AskAsync(payload, askCt))
                {
                    switch (ev)
                    {
                        case AssistantChunk c: sb.Append(c.Text); chunks?.Report(c.Text); break;
                        case AssistantError e: throw new InvalidOperationException(e.Message);
                        case AssistantDone d: done = d; break;
                    }
                }
                if (done is null)
                    throw new InvalidOperationException(
                        "The assistant ended unexpectedly - nothing was saved.");
                answer = sb.ToString();
                backend = done.Backend;
                // Moved inside the try (was after it): an empty/whitespace answer must reset the
                // warm session exactly like AssistantError/no-AssistantDone do - otherwise the
                // NEXT question would silently reuse a session that just produced nothing, which
                // contradicts this class's own "empty answers ... reset the session" contract.
                if (answer.Trim().Length == 0)
                    throw new InvalidOperationException(
                        "The assistant returned an empty answer - nothing was saved.");
            }
            catch
            {
                await ResetSessionAsync();   // a poisoned warm session must not serve the next question
                throw;
            }
            ValidatedAnswer validated = scope.SessionRows is not null
                ? CitationValidator.Validate(answer, scope.SessionRows, scope.SessionId ?? "")
                : MatterCitationValidator.Validate(answer, scope.MatterSummaries ?? []);
            var turn = new AssistantChatTurn(Guid.NewGuid().ToString("N"), _time.GetUtcNow(), question,
                answer, validated.Lines, scope.Model, backend, scope.PromptVersion, scope.ExcerptMode,
                scope.Disclosure, scope.IncludedSessionIds, scope.OmittedSessionIds,
                scope.MissingSummarySessionIds, validated.UnverifiableCount, CudaFellToCpu: cudaFell);
            thread = thread with { Turns = [.. thread.Turns, turn] };
            await _store.SaveAsync(WithThread(log, thread), askCt);
            return turn;
        }
        finally
        {
            lock (_cancelLock) { if (ReferenceEquals(_activeAskCts, askCts)) _activeAskCts = null; }
            _oneAtATime.Release();
        }
    }

    /// <summary>Resolves the ask's target thread (design 2026-07-24 Decision 2): the named thread
    /// if it exists, else the first non-archived thread, else a freshly minted one. The freshly
    /// minted thread is NOT written to the log here - it is a pure in-memory value until the
    /// caller actually persists it (a condense fold or the final answer append), so a failed ask
    /// against an empty store never creates an empty thread on disk.</summary>
    private AssistantChatThread ResolveThread(AssistantChatLog log, string? threadId)
    {
        if (!string.IsNullOrEmpty(threadId))
        {
            var byId = log.Chats.FirstOrDefault(c => c.Id == threadId);
            if (byId is not null) return byId;
        }
        return log.Chats.FirstOrDefault(c => !c.Archived)
            ?? AssistantChatStore.NewThread(AssistantChatStore.MigratedThreadName, _time.GetUtcNow());
    }

    /// <summary>Replaces (or appends) one thread inside a log, by Id. Pure.</summary>
    private static AssistantChatLog WithThread(AssistantChatLog log, AssistantChatThread thread)
    {
        List<AssistantChatThread> chats = [.. log.Chats];
        int i = chats.FindIndex(c => c.Id == thread.Id);
        if (i >= 0) chats[i] = thread; else chats.Add(thread);
        return log with { Chats = chats };
    }

    /// <summary>Budget-driven condense-to-recap policy (design 2026-07-24 Decision 4/brief
    /// algorithm). Folds the OLDEST verbatim turn into the thread's recap, one at a time, until
    /// the history block (recap + remaining verbatim turns) plus the new question fits the
    /// available room under the transcript-context - or there is nothing left to fold. Each
    /// successful fold is persisted immediately (load-modify-save on the target thread) BEFORE
    /// the loop continues or the answer is built: a folded recap is valid regardless of whether
    /// the LATER answer call succeeds (the dropped verbatim turn's content already lives in the
    /// recap, so nothing is lost by persisting early), whereas waiting to persist condense
    /// results until after a successful answer would tie two independent facts together for no
    /// benefit and would also mean a condense that succeeded but was followed by a failed answer
    /// re-attempts (and re-pays for) the same fold on retry. On condense FAILURE (AssistantError /
    /// no Done / cancel) this throws before its own SaveAsync, so that fold persists nothing - the
    /// caller's shared `catch { ResetSessionAsync(); throw; }` then propagates without an answer
    /// turn ever being appended. Guard: if the context alone already leaves no room (available
    /// &lt;= 0), history is skipped entirely (empty block, no loop) rather than folding forever.</summary>
    private async Task<(AssistantChatThread Thread, AssistantChatLog Log, string HistoryBlock)> CondenseIfOverflowingAsync(
        QaScope scope, AssistantChatThread thread, AssistantChatLog log, string question, CancellationToken askCt)
    {
        int budget = _fitsBudgetTokens * TokenBudget.FitsGatePercent / 100;
        int contextTok = TokenBudget.EstimateTokens(scope.ContextText.Length);
        int available = budget - contextTok - QaScopeFactory.MaxAnswerTokens;
        if (available <= 0) return (thread, log, "");   // context alone already fills the budget

        string? recap = thread.Recap;
        string? recapThroughTurnId = thread.RecapThroughTurnId;
        List<AssistantChatTurn> verbatimTurns = [.. thread.Turns];
        string historyBlock;
        while (true)
        {
            historyBlock = AssistantConversation.BuildHistoryBlock(recap, verbatimTurns);
            int historyTok = TokenBudget.EstimateTokens(historyBlock.Length) + TokenBudget.EstimateTokens(question.Length);
            if (historyTok <= available || verbatimTurns.Count == 0) break;

            AssistantChatTurn oldest = verbatimTurns[0];
            recap = await CondenseTurnAsync(scope, recap, oldest, askCt);   // throws + persists nothing on failure
            recapThroughTurnId = oldest.Id;
            verbatimTurns.RemoveAt(0);

            thread = thread with { Recap = recap, RecapThroughTurnId = recapThroughTurnId, Turns = verbatimTurns };
            log = WithThread(log, thread);
            await _store.SaveAsync(log, askCt);   // persist THIS fold before continuing / before the answer
        }
        return (thread, log, historyBlock);
    }

    /// <summary>One condense fold: a SEPARATE transient one-shot chat session (design 2026-07-24
    /// Decision 3) - never the answer warm session/_warmPayload, which is keyed on the
    /// byte-identical scope-context prefix and must not be disturbed by a condense call. Mirrors
    /// QaScopeFactory.Warmup's cheap-prime/real-ask split (review fix): the SAME recap prompt text
    /// is sent twice - once as a WarmupMaxTokens-capped StartAsync priming request (just loads the
    /// model + prefills the KV) and once as a MaxAnswerTokens-capped AskAsync (the real, collected
    /// generation) - reusing the primed KV so the model only actually GENERATES the recap once.
    /// Without this split, StartAsync's production drain (AssistantChatSessionFactory.StartAsync
    /// writes the request and drains it to AssistantDone) would run a full real generation that is
    /// then discarded, and AskAsync would run a second one for the same fold - doubling every
    /// condense fold's cost under the shared engine lease. Mirrors the answer path's event handling
    /// exactly (AssistantError or a stream ending without AssistantDone both throw), and always
    /// disposes the one-shot session.</summary>
    private async Task<string> CondenseTurnAsync(QaScope scope, string? recap, AssistantChatTurn oldest, CancellationToken askCt)
    {
        string prompt = AssistantPrompts.BuildRecapPrompt(recap, oldest);
        string primePayload = AssistantWire.PromptPayload(prompt, QaScopeFactory.WarmupMaxTokens);
        string askPayload = AssistantWire.PromptPayload(prompt, QaScopeFactory.MaxAnswerTokens);
        AssistantRequest recapRequest = scope.WarmupRequest with { PayloadJson = primePayload, KeepAlive = false };
        IAssistantChatSession oneShot = await _factory.StartAsync(recapRequest, askCt);
        try
        {
            var sb = new StringBuilder();
            AssistantDone? done = null;
            await foreach (AssistantEvent ev in oneShot.AskAsync(askPayload, askCt))
            {
                switch (ev)
                {
                    case AssistantChunk c: sb.Append(c.Text); break;
                    case AssistantError e: throw new InvalidOperationException(e.Message);
                    case AssistantDone d: done = d; break;
                }
            }
            if (done is null)
                throw new InvalidOperationException(
                    "The assistant ended unexpectedly while condensing - nothing was saved.");
            return sb.ToString();
        }
        finally
        {
            await oneShot.DisposeAsync();
        }
    }

    /// <summary>Reverse direction of "one heavy engine at a time" (design 7.1): a recording START
    /// cancels the in-flight chat answer (if any) so the assistant yields the engine to live
    /// transcription. Non-blocking + off-thread. The cancelled ask throws OperationCanceledException
    /// BEFORE persisting (nothing saved) and the poisoned warm session is reset via the shared
    /// catch, so the next question re-warms cleanly.</summary>
    public void CancelForRecording()
    {
        CancellationTokenSource? cts;
        lock (_cancelLock) { cts = _activeAskCts; }
        if (cts is null) return;
        try { cts.CancelAfter(TimeSpan.Zero); }
        catch (ObjectDisposedException) { }
    }

    private async Task ResetSessionAsync()
    {
        if (_session is { } s)
        {
            _session = null;
            _warmPayload = null;
            await s.DisposeAsync();
        }
    }

    // Teardown must CANCEL the in-flight ask, not merely wait it out: an ask left running after its
    // VM detaches _service is unreachable by CancelForRecording, so a later recording START could
    // not stop it -> two heavy engines (llama.cpp + live Whisper) during a recording (design 7.1).
    // Cancelling here also correctly discards an answer being generated against a context that is
    // being torn down / has gone stale. The cancel throws OperationCanceledException before
    // AppendAsync (nothing persisted); the ask releases _oneAtATime via its own finally, so the
    // WaitAsync below acquires promptly. Still coordinates with the single-flight guard rather than
    // racing it (the latent _session race) and releases (never Disposes) the semaphore -
    // SemaphoreSlim only needs Dispose() if AvailableWaitHandle was touched (never is here), so
    // leaving it undisposed is benign and avoids an in-flight ask's own `finally { Release(); }`
    // throwing ObjectDisposedException for a request that actually succeeded and persisted.
    public async ValueTask DisposeAsync()
    {
        lock (_cancelLock) { try { _activeAskCts?.CancelAfter(TimeSpan.Zero); } catch (ObjectDisposedException) { } }
        await _oneAtATime.WaitAsync();
        try { await ResetSessionAsync(); }
        finally { _oneAtATime.Release(); }
    }
}
