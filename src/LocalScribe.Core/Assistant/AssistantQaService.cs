using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>Q&amp;A orchestration over the spawn-per-job helper contract (design 2026-07-18
/// sections 7.1 + 7.5 + 7.7; threading + condense added 2026-07-24; Fix A 2026-08-01). One instance
/// per open chat scope. Each ask (and each condense fold) runs as ONE IAssistantJobRunner.RunAsync
/// on a fresh helper - 1x KV, no warm session (the old warm session prefilled the context in a
/// warmup then prefilled it AGAIN on the reused process, doubling the KV and OOMing long chats;
/// KV-prefix reuse was a proven no-op, so nothing but per-message latency is lost - memory
/// [[assistant-grounding-warmup-kv-2026-07-30]]). num_ctx is sized per ask on the FULL wrapped
/// prompt (Fix C). The engine lease (production: the foundation AssistantGate - queued while a
/// recording runs) wraps every model call, INCLUDING any condense folds that ask requires - a
/// no-condense ask still shows exactly one acquire/release pair. A turn is persisted ONLY after a
/// successful AssistantDone - errors, truncated streams and empty answers persist NOTHING; a
/// crashed job never poisons the next ask because RunAsync spawns fresh each time. A single-flight
/// semaphore serializes overlapping AskAsync calls (the store is an unlocked read-modify-write, so
/// two concurrent asks on one service must never interleave). DisposeAsync = teardown on chat
/// close / scope change: it cancels the in-flight ask (killing its helper) and drains the guard.</summary>
public sealed class AssistantQaService : IAsyncDisposable
{
    private readonly IAssistantJobRunner _runner;
    private readonly AssistantChatStore _store;
    private readonly Func<CancellationToken, Task<IAsyncDisposable>> _acquireEngineLease;
    private readonly Func<string, CancellationToken, Task<QaScope>> _scopeFor;
    private readonly TimeProvider _time;
    /// <summary>Test seam (Task 3): overrides the fits-gate budget used by the condense policy so
    /// a small transcript can be made to overflow deterministically without a real 32k transcript.
    /// Production always uses the default (the real 2026-07-18 operating budget).</summary>
    private readonly int _fitsBudgetTokens;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _cancelLock = new();
    private CancellationTokenSource? _activeAskCts;

    public AssistantQaService(IAssistantJobRunner runner, AssistantChatStore store,
        Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease,
        Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time,
        int fitsBudgetTokens = TokenBudget.MaxCtxTokens)
        => (_runner, _store, _acquireEngineLease, _scopeFor, _time, _fitsBudgetTokens)
            = (runner, store, acquireEngineLease, scopeFor, time, fitsBudgetTokens);

    /// <summary>Convenience overload that asks on the default (first non-archived) thread, for
    /// callers/tests without an explicit thread id. Delegates to the 4-arg threaded
    /// AskAsync.</summary>
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
            // The lease wraps the WHOLE model-interaction block - the condense loop (zero or more
            // spawn-per-job folds) plus the answer job - so a no-condense ask still shows exactly one
            // acquire/release pair (locked by Ask_streams...'s order assertion). No inner catch is
            // needed to reset a warm session: Fix A (2026-08-01) spawns a FRESH helper per ask via
            // IAssistantJobRunner (like summaries), so RunAsync owns the process lifetime - a crash,
            // empty answer, or cancel kills that one process and can never poison the next ask.
            await using (IAsyncDisposable lease = await _acquireEngineLease(askCt))
            {
                (thread, log, historyBlock) = await CondenseIfOverflowingAsync(scope, thread, log, question, askCt);

                // Fix C: size num_ctx on the FULL wrapped prompt (preamble+context+history+question),
                // not the transcript body alone (SessionQaContextBuilder sized on body, so a mid/large
                // session's real prompt could exceed num_ctx and overflow). Building the request per
                // ask (Fix A) is what lets us re-pick here on the actual prompt; null (even 64k cannot
                // hold it) clamps to the ladder max so the helper fails closed, never silently truncates.
                string prompt = AssistantPrompts.BuildAnswerPrompt(
                    scope.SpeakerPreamble, scope.ContextText, historyBlock, question);
                AssistantRequest answerRequest = scope.WarmupRequest with
                {
                    Op = "answer",
                    KeepAlive = false,
                    CtxTokens = QaContextLadder.Pick(TokenBudget.EstimateTokens(prompt.Length)) ?? QaContextLadder.CtxSteps[^1],
                    PayloadJson = AssistantWire.PromptPayload(prompt, QaScopeFactory.MaxAnswerTokens),
                };

                var sb = new StringBuilder();
                AssistantDone? done = null;
                cudaFell = false;
                // The floor-fall (cuda->cpu) fires during THIS job's model load (spawn-per-job loads
                // once per ask), so it is captured inline from the run's progress stream rather than
                // from a persistent session. backend=cpu alone cannot tell a fall from a requested-CPU
                // run, so the AssistantProgress(CudaFellPhase) event is the source of truth (design 5).
                await foreach (AssistantEvent ev in _runner.RunAsync(answerRequest, askCt))
                {
                    switch (ev)
                    {
                        case AssistantChunk c: sb.Append(c.Text); chunks?.Report(c.Text); break;
                        case AssistantProgress p when p.Phase == AssistantWire.CudaFellPhase: cudaFell = true; break;
                        case AssistantError e: throw new InvalidOperationException(e.Message);
                        case AssistantDone d: done = d; break;
                    }
                }
                if (done is null)
                    throw new InvalidOperationException(
                        "The assistant ended unexpectedly - nothing was saved.");
                answer = sb.ToString();
                backend = done.Backend;
                if (answer.Trim().Length == 0)
                    throw new InvalidOperationException(
                        "The assistant returned an empty answer - nothing was saved.");
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

    /// <summary>One condense fold: a single spawn-per-job RunAsync (Fix A, 2026-08-01) on a fresh
    /// helper - one real, collected generation of the recap. The old cheap-prime-then-real-ask split
    /// (design 2026-07-24 Decision 3) existed only to stop the warm-session factory's StartAsync
    /// warmup drain from running a full generation that then got discarded before AskAsync ran a
    /// second one; spawn-per-job has no separate warmup, so a fold now generates the recap exactly
    /// once. num_ctx is sized on the recap prompt (Fix C). Mirrors the answer path's event handling
    /// (AssistantError or a stream ending without AssistantDone both throw); RunAsync owns and kills
    /// its own process, so there is nothing to dispose here.</summary>
    private async Task<string> CondenseTurnAsync(QaScope scope, string? recap, AssistantChatTurn oldest, CancellationToken askCt)
    {
        string prompt = AssistantPrompts.BuildRecapPrompt(recap, oldest);
        AssistantRequest recapRequest = scope.WarmupRequest with
        {
            Op = "answer",
            KeepAlive = false,
            CtxTokens = QaContextLadder.Pick(TokenBudget.EstimateTokens(prompt.Length)) ?? QaContextLadder.CtxSteps[^1],
            PayloadJson = AssistantWire.PromptPayload(prompt, QaScopeFactory.MaxAnswerTokens),
        };
        var sb = new StringBuilder();
        AssistantDone? done = null;
        await foreach (AssistantEvent ev in _runner.RunAsync(recapRequest, askCt))
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

    // Teardown must CANCEL the in-flight ask, not merely wait it out: an ask left running after its
    // VM detaches _service is unreachable by CancelForRecording, so a later recording START could
    // not stop it -> two heavy engines (llama.cpp + live Whisper) during a recording (design 7.1).
    // Cancelling here also correctly discards an answer being generated against a context that is
    // being torn down / has gone stale. The cancel throws OperationCanceledException before
    // AppendAsync (nothing persisted) and kills the per-ask helper process (RunAsync's cancel
    // registration); the ask releases _oneAtATime via its own finally, so the WaitAsync below
    // acquires only once that ask has fully unwound. It releases (never Disposes) the semaphore -
    // SemaphoreSlim only needs Dispose() if AvailableWaitHandle was touched (never is here), so
    // leaving it undisposed is benign and avoids an in-flight ask's own `finally { Release(); }`
    // throwing ObjectDisposedException for a request that actually succeeded and persisted.
    public async ValueTask DisposeAsync()
    {
        lock (_cancelLock) { try { _activeAskCts?.CancelAfter(TimeSpan.Zero); } catch (ObjectDisposedException) { } }
        await _oneAtATime.WaitAsync();
        _oneAtATime.Release();
    }
}
