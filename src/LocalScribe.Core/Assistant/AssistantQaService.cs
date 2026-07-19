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

    public AssistantQaService(IAssistantChatSessionFactory factory, AssistantChatStore store,
        Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease,
        Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time)
        => (_factory, _store, _acquireEngineLease, _scopeFor, _time)
            = (factory, store, acquireEngineLease, scopeFor, time);

    public async Task<AssistantChatTurn> AskAsync(string question, IProgress<string>? chunks,
        CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            QaScope scope = await _scopeFor(question, ct);
            if (scope.NoMatches)
                throw new InvalidOperationException(
                    "There is nothing to answer from in this scope yet (no matching excerpts, or no session summaries generated).");
            string answer;
            string backend;
            try
            {
                await using IAsyncDisposable lease = await _acquireEngineLease(ct);
                if (_session is null
                    || !string.Equals(_warmPayload, scope.WarmupRequest.PayloadJson, StringComparison.Ordinal))
                {
                    await ResetSessionAsync();
                    _session = await _factory.StartAsync(scope.WarmupRequest, ct);
                    _warmPayload = scope.WarmupRequest.PayloadJson;
                }
                var sb = new StringBuilder();
                AssistantDone? done = null;
                // Contract resolution #4: send the FULL prompt every ask, byte-identical up to
                // the question tail, so the helper's KV prefix (prefilled by the warmup) is
                // reused - only the question's own tail is new prefill.
                string payload = AssistantWire.PromptPayload(
                    AssistantPrompts.BuildAnswerPrompt(scope.SpeakerPreamble, scope.ContextText, question),
                    QaScopeFactory.MaxAnswerTokens);
                await foreach (AssistantEvent ev in _session.AskAsync(payload, ct))
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
                scope.MissingSummarySessionIds, validated.UnverifiableCount);
            await _store.AppendAsync(turn, ct);
            return turn;
        }
        finally
        {
            _oneAtATime.Release();
        }
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

    // Coordinates with the single-flight guard rather than racing it: waits out any in-flight
    // AskAsync so the session teardown below cannot run concurrently with one (the latent
    // _session race), then releases (never Disposes) the semaphore - SemaphoreSlim only needs
    // Dispose() if AvailableWaitHandle was touched (never is here), so leaving it undisposed is
    // benign and avoids an in-flight ask's own `finally { Release(); }` throwing
    // ObjectDisposedException for a request that actually succeeded and persisted.
    public async ValueTask DisposeAsync()
    {
        await _oneAtATime.WaitAsync();
        try { await ResetSessionAsync(); }
        finally { _oneAtATime.Release(); }
    }
}
