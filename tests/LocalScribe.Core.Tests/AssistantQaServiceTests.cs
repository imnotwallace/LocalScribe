using System.Runtime.CompilerServices;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Tests;

public class AssistantQaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private AssistantChatStore Store => new(Path.Combine(_root, "assistant", "chats.json"));

    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    // OVERRIDE 1/5: QaScope carries SpeakerPreamble + ContextText (trailing "", "" stand-ins -
    // the value only matters for what the fake receives as the ask payload; behavior assertions
    // below are unchanged from the plan).
    private static QaScope SessionScope(IReadOnlyList<DisplayRow> rows, string payload = "P1") => new(
        new AssistantRequest(Op: "answer", ModelPath: @"C:\models\m.gguf", CtxTokens: 8192,
            Backend: "auto", KeepAlive: true, PayloadJson: payload),
        "m.gguf", "3", false, null, false, "s1", rows, null, ["s1"], [], [], "", "");

    private static IReadOnlyList<AssistantEvent> Script(params AssistantEvent[] events) => events;

    private sealed class CollectingProgress : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) => Items.Add(value);
    }

    private sealed class FakeLease(List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { order.Add("release"); return ValueTask.CompletedTask; }
    }

    private (AssistantQaService Svc, FakeAssistantChatSessionFactory Factory, AssistantChatStore Store, List<string> Order)
        Make(Func<string, CancellationToken, Task<QaScope>> scopeFor)
    {
        var factory = new FakeAssistantChatSessionFactory();
        var store = Store;
        var order = new List<string>();
        var svc = new AssistantQaService(factory, store,
            ct => { order.Add("acquire"); return Task.FromResult<IAsyncDisposable>(new FakeLease(order)); },
            scopeFor, TimeProvider.System);
        return (svc, factory, store, order);
    }

    [Fact]
    public async Task Ask_streams_chunks_validates_citations_and_persists_the_turn()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, order) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(
            new AssistantChunk("The parties agreed to settle for ten thousand "),
            new AssistantChunk("dollars [00:01:05]"),
            new AssistantDone("cpu", 100, 42)));

        var progress = new CollectingProgress();
        var turn = await svc.AskAsync("what was the settlement", progress, CancellationToken.None);

        Assert.Equal(new[] { "The parties agreed to settle for ten thousand ", "dollars [00:01:05]" },
            progress.Items);
        Assert.Equal("cpu", turn.Backend);                       // AssistantDone provenance, not the request
        Assert.Equal("m.gguf", turn.Model);
        Assert.Equal(0, turn.UnverifiableClaims);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal(3, chip.Seq);
        Assert.Equal(new[] { "acquire", "release" }, order);     // lease wrapped the model call
        var warmup = Assert.Single(factory.Warmups);
        Assert.True(warmup.KeepAlive);                           // warm helper (design 7.1)
        Assert.Contains("what was the settlement", Assert.Single(factory.Sessions).Questions.Single());
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task Warm_session_is_reused_while_the_context_payload_is_identical()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, "SAME")));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("A [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("first", null, CancellationToken.None);
        factory.Sessions[0].Scripted.Enqueue(Script(new AssistantChunk("B [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("second", null, CancellationToken.None);

        Assert.Single(factory.Warmups);                          // ONE prefill - KV reuse (design 7.1)
        Assert.Equal(2, factory.Sessions[0].Questions.Count);
        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Turns.Count);
    }

    [Fact]
    public async Task Context_change_rebuilds_the_warm_session()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        string payload = "P1";
        var (svc, factory, _, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, payload)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("A [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("B [00:01:05]"), new AssistantDone("cpu", 1, 1)));

        await svc.AskAsync("first", null, CancellationToken.None);
        payload = "P2";                                          // transcript changed -> new context
        await svc.AskAsync("second", null, CancellationToken.None);

        Assert.Equal(2, factory.Warmups.Count);
        Assert.True(factory.Sessions[0].Disposed);               // stale prefill torn down
    }

    [Fact]
    public async Task Error_event_persists_nothing_and_resets_the_session()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("half an ans"), new AssistantError("helper crashed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("helper crashed", ex.Message);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // section 7.7: nothing persisted
        Assert.True(factory.Sessions[0].Disposed);               // poisoned session never serves again

        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("retry", null, CancellationToken.None);
        Assert.Equal(2, factory.Warmups.Count);                  // re-warmed cleanly
    }

    // Review-round fix (Important #1): the empty-answer check used to sit AFTER the inner
    // try/catch that resets a poisoned session, so it never ran through the shared
    // `catch { ResetSessionAsync(); throw; }` - an empty/whitespace AssistantDone threw
    // (nothing persisted, correctly) but silently left the poisoned warm session in place for
    // the NEXT question. Mirrors Error_event_persists_nothing_and_resets_the_session.
    [Fact]
    public async Task Empty_answer_persists_nothing_and_resets_the_session()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("   "), new AssistantDone("cpu", 1, 1)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("empty answer", ex.Message);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // section 7.7: nothing persisted
        Assert.True(factory.Sessions[0].Disposed);               // poisoned session never serves again

        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("retry", null, CancellationToken.None);
        Assert.Equal(2, factory.Warmups.Count);                  // re-warmed cleanly
    }

    [Fact]
    public async Task Stream_ending_without_done_is_an_error_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("half")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
        Assert.True(factory.Sessions[0].Disposed);               // MINOR: this path already reset correctly - was under-asserted
    }

    [Fact]
    public async Task NoMatches_scope_refuses_without_touching_the_model()
    {
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 32768,
                Backend: "auto", KeepAlive: true, PayloadJson: ""),
            "m.gguf", "3", true, ExcerptContextBuilder.DisclosureText, NoMatches: true,
            "s1", [], null, ["s1"], [], [], "", "");
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty(factory.Warmups);                           // the model was never engaged
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task Matter_scope_validates_against_the_included_summaries()
    {
        var summaries = new[]
        {
            new MatterSummarySource("a", "Session a", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                "The parties agreed to settle for ten thousand dollars [00:01:05]", false),
        };
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
                Backend: "auto", KeepAlive: true, PayloadJson: "M1"),
            "m.gguf", "3", false, null, false, null, null, summaries, ["a"], ["b"], ["c"], "", "");
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(scope));
        factory.ScriptPerSession.Enqueue(Script(
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cuda", 1, 1)));

        var turn = await svc.AskAsync("what was agreed", null, CancellationToken.None);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("a", chip.SessionId);
        Assert.Equal(-1, chip.Seq);
        // Coverage disclosure survives into history. Split out of a single ValueTuple-of-arrays
        // assertion (the plan's original form): ValueTuple<string[],...>.Equals falls back to
        // reference equality per element, so it never compares equal across distinct array
        // instances even with identical contents - an xUnit/BCL mechanics trap unrelated to this
        // task's contract adaptation. Plain Assert.Equal on each list DOES compare element-wise.
        Assert.Equal(new[] { "a" }, turn.IncludedSessionIds);
        Assert.Equal(new[] { "b" }, turn.OmittedSessionIds);
        Assert.Equal(new[] { "c" }, turn.MissingSummarySessionIds);
    }

    // OVERRIDE 4: single-flight guard. AssistantChatStore.AppendAsync is an unlocked
    // read-modify-write and the warm-session state (_session/_warmPayload) is mutable, so two
    // concurrent AskAsync calls on one service must never interleave. This uses a bespoke
    // (non-shared) test double whose FIRST AskAsync call blocks on a TaskCompletionSource until
    // released - it is deliberately NOT part of AssistantChatFakes.cs (kept verbatim per the
    // brief) since none of the shared fakes can pause mid-stream. All the seams up to the
    // blocking await resolve synchronously (Task.FromResult everywhere), so by the time
    // svc.AskAsync(...) returns its Task the session's AskAsync has already been entered -
    // asserting CallCount immediately (no Task.Delay) is deterministic, not a race.
    private sealed class BlockingThenSession : IAssistantChatSession
    {
        private readonly TaskCompletionSource _gate = new();
        public int CallCount { get; private set; }
        public List<string> Questions { get; } = [];

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            Questions.Add(questionPayloadJson);
            if (CallCount == 1) await _gate.Task;
            // Honor a teardown/preempt cancel DETERMINISTICALLY (mirrors BlockingChatSession) so the
            // Dispose-racing test can't hinge on an unbounded CancelAfter(0)-vs-inline-Release race.
            // A no-op for the None-token callers (the single-flight test).
            ct.ThrowIfCancellationRequested();
            yield return new AssistantChunk($"answer {CallCount} [00:01:05]");
            yield return new AssistantDone("cpu", 1, 1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleSessionFactory(IAssistantChatSession session) : IAssistantChatSessionFactory
    {
        public List<AssistantRequest> Warmups { get; } = [];

        public Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
        {
            Warmups.Add(warmupRequest);
            return Task.FromResult(session);
        }
    }

    // Branch-7 fix: chat recording-preemption (design 7.1 reverse direction, mirrors the
    // SummarizationService.CancelForRecording fix already merged for the summarizer). Yields one
    // chunk (so the ask is genuinely mid-stream, past the lease and past warmup) then blocks
    // forever UNLESS the token it was actually given cancels - so this session doubles as the
    // mutation discriminator: if AssistantQaService still threaded the outer (never-cancelled)
    // `ct` instead of the per-ask linked `askCt` into _session.AskAsync, this Task.Delay would
    // never observe the CancelForRecording() call and the test would hang until its bounded
    // WaitAsync times out.
    private sealed class BlockingChatSession : IAssistantChatSession
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AssistantChunk("partial");
            await Task.Delay(Timeout.Infinite, ct);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // Signals on the FIRST streamed chunk so the test can wait (bounded) until the ask is
    // genuinely mid-run before calling CancelForRecording - a plain List<string> collector (like
    // CollectingProgress above) has nothing to await on.
    private sealed class FirstChunkSignal : IProgress<string>
    {
        private readonly TaskCompletionSource _first = new();
        public Task FirstChunk => _first.Task;
        public void Report(string value) => _first.TrySetResult();
    }

    [Fact]
    public async Task Recording_start_cancels_the_in_flight_ask_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var session = new BlockingChatSession();
        var factory = new SingleSessionFactory(session);
        var store = Store;
        var svc = new AssistantQaService(factory, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        var progress = new FirstChunkSignal();
        Task<AssistantChatTurn> ask = svc.AskAsync("q", progress, CancellationToken.None);
        await progress.FirstChunk.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        svc.CancelForRecording();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // nothing persisted on cancel
        Assert.True(session.Disposed);                                        // poisoned session reset
    }

    // Branch-7 whole-branch-review fix (the Important): a DETACHED in-flight ask must still be
    // cancellable from teardown. AssistantChatViewModel.InvalidateContext nulls its own _service
    // reference and fire-forgets DisposeAsync WITHOUT going through CancelForRecording - so if
    // DisposeAsync only waited out the single-flight guard (the old code), an ask left running
    // past a detach would be unreachable by any later CancelForRecording (null _service = no-op)
    // and would keep contending with llama.cpp through a recording (design 7.1). This uses the
    // same BlockingChatSession/SingleSessionFactory/FirstChunkSignal doubles as the
    // CancelForRecording test above - BlockingChatSession only unblocks when the token it was
    // actually given cancels, so it doubles as the mutation discriminator: against the old
    // DisposeAsync (which never cancels _activeAskCts, only awaits _oneAtATime), the ask never
    // observes cancellation, DisposeAsync's own WaitAsync() never acquires (the ask holds the
    // guard), and the whole test hangs until the bounded WaitAsync times out.
    [Fact]
    public async Task Dispose_cancels_the_in_flight_ask_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var session = new BlockingChatSession();
        var factory = new SingleSessionFactory(session);
        var store = Store;
        var svc = new AssistantQaService(factory, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        var progress = new FirstChunkSignal();
        Task<AssistantChatTurn> ask = svc.AskAsync("q", progress, CancellationToken.None);
        await progress.FirstChunk.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        await svc.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // simulates the detach path

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // nothing persisted on cancel
        Assert.True(session.Disposed);                                        // poisoned session reset
    }

    [Fact]
    public void CancelForRecording_with_no_active_ask_is_a_safe_no_op()
    {
        var (svc, _, _, _) = Make((q, ct) => Task.FromResult(SessionScope([])));
        svc.CancelForRecording();   // nothing running - must not throw
    }

    [Fact]
    public async Task Overlapping_asks_are_serialized_not_interleaved()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var session = new BlockingThenSession();
        var factory = new SingleSessionFactory(session);
        var store = Store;
        var svc = new AssistantQaService(factory, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        Task<AssistantChatTurn> ask1 = svc.AskAsync("first", null, CancellationToken.None);
        Assert.Equal(1, session.CallCount);          // ask1 has entered the session and is blocked

        Task<AssistantChatTurn> ask2 = svc.AskAsync("second", null, CancellationToken.None);
        Assert.Equal(1, session.CallCount);          // ask2 did NOT start - serialized behind ask1

        session.Release();
        AssistantChatTurn turn1 = await ask1;
        AssistantChatTurn turn2 = await ask2;

        Assert.Equal(2, session.CallCount);
        Assert.Equal("first", turn1.Question);
        Assert.Equal("second", turn2.Question);
        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Turns.Count);   // both persisted, in order
    }

    // Review-round fix (Important #2): DisposeAsync used to call _oneAtATime.Dispose() without
    // first acquiring the guard, racing an in-flight AskAsync. If DisposeAsync ran while an ask
    // was mid-stream (chat tab closed mid-answer), the ask would still finish and persist
    // successfully, then hit its OWN `finally { _oneAtATime.Release(); }` on an
    // already-disposed semaphore -> ObjectDisposedException surfaced for a request that actually
    // SUCCEEDED. The fix makes DisposeAsync wait out the guard (never Dispose() it) so it cannot
    // race a still-running ask - that invariant (no ObjectDisposedException from the semaphore)
    // is UNCHANGED and still guarded below.
    //
    // Superseded-in-part by the branch-7 whole-branch fix (the Important above): DisposeAsync now
    // also cancels the in-flight ask instead of letting it run to completion, so this test's
    // ORIGINAL "persisted despite the race" assertion is no longer the correct contract - per
    // design 7.1, DisposeAsync is teardown (the context is going away / has gone stale), so a raced
    // ask must be discarded, not persisted, even if (as here) it is almost done. Updated to assert
    // the new contract: no ObjectDisposedException, the raced ask is cancelled, nothing persists.
    [Fact]
    public async Task Dispose_racing_an_in_flight_ask_cancels_it_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var session = new BlockingThenSession();
        var factory = new SingleSessionFactory(session);
        var store = Store;
        var svc = new AssistantQaService(factory, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        Task<AssistantChatTurn> ask = svc.AskAsync("first", null, CancellationToken.None);
        Assert.Equal(1, session.CallCount);          // ask has entered the session and is blocked

        ValueTask disposeVt = svc.DisposeAsync();    // must not throw ObjectDisposedException

        session.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        await disposeVt.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // discarded, not persisted
    }
}
