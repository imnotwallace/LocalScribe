using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>Q&amp;A orchestration over the foundation warm-helper contract (design 2026-07-18
/// sections 7.1 + 7.5 + 7.7). One instance per open chat scope. The warm session is REUSED
/// while the warmup payload is byte-identical (KV reuse - follow-up questions skip the
/// re-prefill) and rebuilt when the context changes; the engine lease (production: the
/// foundation AssistantGate - queued while a recording runs) wraps every model call; a turn is
/// persisted ONLY after a successful AssistantDone - errors, truncated streams and empty
/// answers persist NOTHING and reset the session. A single-flight semaphore (Task-6 reviewer +
/// branch-6 note N3) serializes overlapping AskAsync calls - AssistantChatStore.AppendAsync is
/// an unlocked read-modify-write and the warm-session fields are mutable, so two concurrent
/// asks on one service must never interleave. DisposeAsync = teardown on chat close / scope
/// change; the 5-minute idle teardown is the foundation session's own duty.</summary>
public sealed class AssistantQaService : IAsyncDisposable
{
    private readonly IAssistantChatSessionFactory _factory;
    private readonly AssistantChatStore _store;
    private readonly Func<CancellationToken, Task<IAsyncDisposable>> _acquireEngineLease;
    private readonly Func<string, CancellationToken, Task<QaScope>> _scopeFor;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private IAssistantChatSession? _session;
    private string? _warmPayload;
    private readonly object _cancelLock = new();
    private CancellationTokenSource? _activeAskCts;

    public AssistantQaService(IAssistantChatSessionFactory factory, AssistantChatStore store,
        Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease,
        Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time)
        => (_factory, _store, _acquireEngineLease, _scopeFor, _time)
            = (factory, store, acquireEngineLease, scopeFor, time);

    public async Task<AssistantChatTurn> AskAsync(string question, IProgress<string>? chunks,
        CancellationToken ct)
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
            string answer;
            string backend;
            bool cudaFell;
            try
            {
                await using IAsyncDisposable lease = await _acquireEngineLease(askCt);
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
                // the question tail, so the helper's KV prefix (prefilled by the warmup) is
                // reused - only the question's own tail is new prefill.
                string payload = AssistantWire.PromptPayload(
                    AssistantPrompts.BuildAnswerPrompt(scope.SpeakerPreamble, scope.ContextText, question),
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
            await _store.AppendAsync(turn, askCt);
            return turn;
        }
        finally
        {
            lock (_cancelLock) { if (ReferenceEquals(_activeAskCts, askCts)) _activeAskCts = null; }
            _oneAtATime.Release();
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
