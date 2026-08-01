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

    // Fix A (2026-08-01, spawn-per-job): the service no longer keeps a warm session, so the scope's
    // WarmupRequest is only a TEMPLATE (ModelPath/Backend) - the service overrides Op/KeepAlive/
    // CtxTokens/PayloadJson per ask. SpeakerPreamble + ContextText ("" here) are the prompt
    // ingredients the service rebuilds into the FULL per-ask prompt the fake runner receives.
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

    private (AssistantQaService Svc, FakeAssistantJobRunner Runner, AssistantChatStore Store, List<string> Order)
        Make(Func<string, CancellationToken, Task<QaScope>> scopeFor)
    {
        var runner = new FakeAssistantJobRunner();
        var store = Store;
        var order = new List<string>();
        var svc = new AssistantQaService(runner, store,
            ct => { order.Add("acquire"); return Task.FromResult<IAsyncDisposable>(new FakeLease(order)); },
            scopeFor, TimeProvider.System);
        return (svc, runner, store, order);
    }

    [Fact]
    public async Task Ask_streams_chunks_validates_citations_and_persists_the_turn()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, order) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(
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
        var req = Assert.Single(runner.Requests);                // ONE spawn-per-job run (Fix A: fresh helper, 1x KV)
        Assert.False(req.KeepAlive);                             // no warm session
        Assert.Equal("answer", req.Op);
        Assert.Contains("what was the settlement", req.PayloadJson);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    // The per-job model LOAD carries the CUDA-fell verdict (spawn-per-job loads once per ask), so a
    // degraded chat answer is never silently labelled plain "CPU". Captured inline from the run's
    // AssistantProgress stream, not from a persistent session.
    [Fact]
    public async Task Turn_records_the_cuda_fall()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(
            new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0),   // floor-fall during this job's model load
            new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));

        var turn = await svc.AskAsync("q", null, CancellationToken.None);
        Assert.True(turn.CudaFellToCpu);
        Assert.True((await store.LoadAsync(CancellationToken.None)).Turns.Single().CudaFellToCpu);   // survives the sidecar round trip
    }

    [Fact]
    public async Task Turn_without_a_fall_is_not_marked_degraded()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cuda", 1, 1)));

        var turn = await svc.AskAsync("q", null, CancellationToken.None);
        Assert.False(turn.CudaFellToCpu);
        Assert.False((await store.LoadAsync(CancellationToken.None)).Turns.Single().CudaFellToCpu);
    }

    // Fix A: each ask spawns a FRESH helper - there is no warm KV to reuse (the warm session's
    // never-reset KV doubled the context and OOMed a 21-min chat, memory
    // [[assistant-grounding-warmup-kv-2026-07-30]]). Two asks -> two independent runs, both persist.
    [Fact]
    public async Task Each_ask_spawns_a_fresh_helper_no_warm_reuse()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, "SAME")));
        runner.Scripts.Enqueue(Script(new AssistantChunk("A [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        runner.Scripts.Enqueue(Script(new AssistantChunk("B [00:01:05]"), new AssistantDone("cpu", 1, 1)));

        await svc.AskAsync("first", null, CancellationToken.None);
        await svc.AskAsync("second", null, CancellationToken.None);

        Assert.Equal(2, runner.Requests.Count);                  // one fresh helper per ask, never a reused warm KV
        Assert.All(runner.Requests, r => Assert.False(r.KeepAlive));
        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Turns.Count);
    }

    [Fact]
    public async Task Error_event_persists_nothing_and_the_next_ask_recovers()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(new AssistantChunk("half an ans"), new AssistantError("helper crashed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("helper crashed", ex.Message);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // section 7.7: nothing persisted

        runner.Scripts.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("retry", null, CancellationToken.None);
        Assert.Equal(2, runner.Requests.Count);                  // fresh helper each time - a crash never poisons the next ask
    }

    [Fact]
    public async Task Empty_answer_persists_nothing_and_the_next_ask_recovers()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(new AssistantChunk("   "), new AssistantDone("cpu", 1, 1)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("empty answer", ex.Message);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // section 7.7: nothing persisted

        runner.Scripts.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("retry", null, CancellationToken.None);
        Assert.Equal(2, runner.Requests.Count);                  // re-ran cleanly on a fresh helper
    }

    [Fact]
    public async Task Stream_ending_without_done_is_an_error_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        runner.Scripts.Enqueue(Script(new AssistantChunk("half")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task NoMatches_scope_refuses_without_touching_the_model()
    {
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 32768,
                Backend: "auto", KeepAlive: true, PayloadJson: ""),
            "m.gguf", "3", true, ExcerptContextBuilder.DisclosureText, NoMatches: true,
            "s1", [], null, ["s1"], [], [], "", "");
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty(runner.Requests);                           // the model was never engaged
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
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(scope));
        runner.Scripts.Enqueue(Script(
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cuda", 1, 1)));

        var turn = await svc.AskAsync("what was agreed", null, CancellationToken.None);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("a", chip.SessionId);
        Assert.Equal(-1, chip.Seq);
        Assert.Equal(new[] { "a" }, turn.IncludedSessionIds);
        Assert.Equal(new[] { "b" }, turn.OmittedSessionIds);
        Assert.Equal(new[] { "c" }, turn.MissingSummarySessionIds);
    }

    // Blocking runner for the preemption tests: yields one chunk (so the ask is genuinely mid-stream,
    // past the lease) then blocks forever UNLESS the token it was given cancels - so it doubles as the
    // mutation discriminator (if the service threaded the outer never-cancelled ct instead of the
    // per-ask askCt, this would never observe CancelForRecording()/DisposeAsync and the test would hang).
    private sealed class BlockingRunner : IAssistantJobRunner
    {
        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AssistantChunk("partial");
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    // Signals on the FIRST streamed chunk so a test can wait (bounded) until the ask is genuinely
    // mid-run before cancelling it - a plain collector has nothing to await on.
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
        var store = Store;
        var svc = new AssistantQaService(new BlockingRunner(), store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        var progress = new FirstChunkSignal();
        Task<AssistantChatTurn> ask = svc.AskAsync("q", progress, CancellationToken.None);
        await progress.FirstChunk.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        svc.CancelForRecording();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // nothing persisted on cancel
    }

    [Fact]
    public async Task Dispose_cancels_the_in_flight_ask_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var store = Store;
        var svc = new AssistantQaService(new BlockingRunner(), store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        var progress = new FirstChunkSignal();
        Task<AssistantChatTurn> ask = svc.AskAsync("q", progress, CancellationToken.None);
        await progress.FirstChunk.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        await svc.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // simulates the detach path

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // nothing persisted on cancel
    }

    [Fact]
    public void CancelForRecording_with_no_active_ask_is_a_safe_no_op()
    {
        var (svc, _, _, _) = Make((q, ct) => Task.FromResult(SessionScope([])));
        svc.CancelForRecording();   // nothing running - must not throw
    }

    // Runner whose FIRST RunAsync blocks on a gate until Release() - the single-flight discriminator.
    // All the seams up to RunAsync resolve synchronously, so asserting CallCount right after starting
    // an ask is deterministic (not a race). Honors a cancel deterministically for the dispose-race test.
    private sealed class BlockingThenRunner : IAssistantJobRunner
    {
        private readonly TaskCompletionSource _gate = new();
        public int CallCount { get; private set; }
        public List<string> Payloads { get; } = [];
        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            Payloads.Add(request.PayloadJson);
            if (CallCount == 1) await _gate.Task;
            ct.ThrowIfCancellationRequested();
            yield return new AssistantChunk($"answer {CallCount} [00:01:05]");
            yield return new AssistantDone("cpu", 1, 1);
        }
    }

    [Fact]
    public async Task Overlapping_asks_are_serialized_not_interleaved()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var runner = new BlockingThenRunner();
        var store = Store;
        var svc = new AssistantQaService(runner, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        Task<AssistantChatTurn> ask1 = svc.AskAsync("first", null, CancellationToken.None);
        Assert.Equal(1, runner.CallCount);           // ask1 has entered the runner and is blocked

        Task<AssistantChatTurn> ask2 = svc.AskAsync("second", null, CancellationToken.None);
        Assert.Equal(1, runner.CallCount);           // ask2 did NOT start - serialized behind ask1

        runner.Release();
        AssistantChatTurn turn1 = await ask1;
        AssistantChatTurn turn2 = await ask2;

        Assert.Equal(2, runner.CallCount);
        Assert.Equal("first", turn1.Question);
        Assert.Equal("second", turn2.Question);
        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Turns.Count);   // both persisted, in order
    }

    [Fact]
    public async Task Dispose_racing_an_in_flight_ask_cancels_it_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var runner = new BlockingThenRunner();
        var store = Store;
        var svc = new AssistantQaService(runner, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);

        Task<AssistantChatTurn> ask = svc.AskAsync("first", null, CancellationToken.None);
        Assert.Equal(1, runner.CallCount);           // ask has entered the runner and is blocked

        ValueTask disposeVt = svc.DisposeAsync();    // must not throw ObjectDisposedException

        runner.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask).WaitAsync(TimeSpan.FromSeconds(5));
        await disposeVt.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // discarded, not persisted
    }

    // Design 2026-07-24: the SECOND ask's payload must carry the first Q&A so the model has memory of
    // the thread. With spawn-per-job each ask is a separate run, so runner.Requests[1] holds the
    // follow-up prompt (context + prior turn + new question).
    [Fact]
    public async Task Follow_up_includes_prior_turn_in_the_prompt()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, _, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, "SAME")));
        runner.Scripts.Enqueue(Script(
            new AssistantChunk("First answer [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("first question", null, CancellationToken.None);

        runner.Scripts.Enqueue(Script(
            new AssistantChunk("Second answer [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("second question", null, CancellationToken.None);

        string secondPayload = runner.Requests[1].PayloadJson;
        Assert.Contains("first question", secondPayload);
        Assert.Contains("First answer", secondPayload);
    }

    // Design 2026-07-24: AskAsync(question, threadId, ...) appends to THAT thread's Turns - not a flat
    // log - leaving an unrelated thread in the same store untouched.
    [Fact]
    public async Task Turn_is_appended_to_the_named_thread()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, runner, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        var threadA = AssistantChatStore.NewThread("Thread A", DateTimeOffset.UtcNow);
        var threadB = AssistantChatStore.NewThread("Thread B", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [threadA, threadB] }, CancellationToken.None);
        runner.Scripts.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));

        var turn = await svc.AskAsync("q", threadB.Id, null, CancellationToken.None);

        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(log.Chats.Single(c => c.Id == threadA.Id).Turns);
        var savedB = log.Chats.Single(c => c.Id == threadB.Id);
        Assert.Single(savedB.Turns);
        Assert.Equal(turn.Id, savedB.Turns[0].Id);
    }

    // Design 2026-07-24: budget-driven condense-to-recap. A tiny fitsBudgetTokens (the test seam)
    // forces the third ask to fold the oldest of two pre-seeded verbatim turns into a recap before
    // answering: Recap becomes non-empty, RecapThroughTurnId advances, that turn drops out of Turns,
    // and the scope's context still reaches the answer run.
    [Fact]
    public async Task Overflow_condenses_oldest_turns_into_recap_and_keeps_context()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        const string ctxText = "XCTXMARK";
        var scope = SessionScope(rows) with { ContextText = ctxText };
        var runner = new FakeAssistantJobRunner();
        var store = Store;
        var order = new List<string>();
        var svc = new AssistantQaService(runner, store,
            ct => { order.Add("acquire"); return Task.FromResult<IAsyncDisposable>(new FakeLease(order)); },
            (q, ct) => Task.FromResult(scope), TimeProvider.System, fitsBudgetTokens: 3000);

        var oldTurn = new AssistantChatTurn("old", DateTimeOffset.UtcNow.AddMinutes(-10),
            new string('a', 900), new string('b', 900), [], "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 0);
        var recentTurn = new AssistantChatTurn("recent", DateTimeOffset.UtcNow.AddMinutes(-5),
            new string('c', 900), new string('d', 900), [], "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 0);
        var thread = AssistantChatStore.NewThread(AssistantChatStore.MigratedThreadName, DateTimeOffset.UtcNow.AddMinutes(-20))
            with { Turns = [oldTurn, recentTurn] };
        await store.SaveAsync(new AssistantChatLog { Chats = [thread] }, CancellationToken.None);

        runner.Scripts.Enqueue(Script(
            new AssistantChunk("condensed recap of the earlier exchange"), new AssistantDone("cpu", 1, 1)));   // condense fold
        runner.Scripts.Enqueue(Script(
            new AssistantChunk("final answer [00:01:05]"), new AssistantDone("cpu", 1, 1)));                   // answer

        var turn = await svc.AskAsync("third question", thread.Id, null, CancellationToken.None);

        var savedThread = (await store.LoadAsync(CancellationToken.None)).Chats.Single(c => c.Id == thread.Id);
        Assert.NotNull(savedThread.Recap);
        Assert.Contains("condensed recap", savedThread.Recap);
        Assert.Equal(oldTurn.Id, savedThread.RecapThroughTurnId);
        Assert.DoesNotContain(savedThread.Turns, t => t.Id == oldTurn.Id);
        Assert.Contains(savedThread.Turns, t => t.Id == recentTurn.Id);
        Assert.Contains(savedThread.Turns, t => t.Id == turn.Id);
        Assert.Equal(2, runner.Requests.Count);                  // 1 condense fold + 1 answer, each a spawn-per-job run
        Assert.Contains(ctxText, runner.Requests[1].PayloadJson); // context still reaches the model on the answer
        Assert.Equal(new[] { "acquire", "release" }, order);     // ONE lease pair even though it condensed

        // Fix A: a condense fold is now a SINGLE RunAsync (real generation, MaxAnswerTokens cap). The
        // old cheap-prime/real-ask split existed only to avoid the warm session's warmup double-drain,
        // which spawn-per-job removes.
        string expectedRecapPrompt = AssistantPrompts.BuildRecapPrompt(null, oldTurn);
        string expectedAskPayload = AssistantWire.PromptPayload(expectedRecapPrompt, QaScopeFactory.MaxAnswerTokens);
        Assert.Equal(expectedAskPayload, runner.Requests[0].PayloadJson);   // condense fold's payload
    }

    // Design 2026-07-24: a condense call that errors must persist nothing - no partial recap, no
    // dropped verbatim turn, no appended answer turn.
    [Fact]
    public async Task Condense_failure_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        const string ctxText = "XCTXMARK";
        var scope = SessionScope(rows) with { ContextText = ctxText };
        var runner = new FakeAssistantJobRunner();
        var store = Store;
        var svc = new AssistantQaService(runner, store,
            ct => Task.FromResult<IAsyncDisposable>(new FakeLease([])),
            (q, ct) => Task.FromResult(scope), TimeProvider.System, fitsBudgetTokens: 3000);

        var oldTurn = new AssistantChatTurn("old", DateTimeOffset.UtcNow.AddMinutes(-10),
            new string('a', 900), new string('b', 900), [], "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 0);
        var recentTurn = new AssistantChatTurn("recent", DateTimeOffset.UtcNow.AddMinutes(-5),
            new string('c', 900), new string('d', 900), [], "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 0);
        var thread = AssistantChatStore.NewThread(AssistantChatStore.MigratedThreadName, DateTimeOffset.UtcNow.AddMinutes(-20))
            with { Turns = [oldTurn, recentTurn] };
        await store.SaveAsync(new AssistantChatLog { Chats = [thread] }, CancellationToken.None);

        runner.Scripts.Enqueue(Script(new AssistantError("condense crashed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("third question", thread.Id, null, CancellationToken.None));
        Assert.Contains("condense crashed", ex.Message);

        var savedThread = (await store.LoadAsync(CancellationToken.None)).Chats.Single(c => c.Id == thread.Id);
        Assert.Null(savedThread.Recap);
        Assert.Null(savedThread.RecapThroughTurnId);
        Assert.Equal(new[] { oldTurn.Id, recentTurn.Id }, savedThread.Turns.Select(t => t.Id));
    }
}
