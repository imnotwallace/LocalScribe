using System.Runtime.CompilerServices;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class SummarizationServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-sumsvc-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;
    private Settings _settings = new();

    public SummarizationServiceTests() { _paths = new StoragePaths(_root); _store = new SummaryStore(_paths); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeRunner(Func<AssistantRequest, IEnumerable<AssistantEvent>> script) : IAssistantJobRunner
    {
        public List<AssistantRequest> Requests { get; } = [];
        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            foreach (var e in script(request)) { await Task.Yield(); yield return e; }
        }
    }

    private static readonly AssistantModelInfo Qwen4B =
        new("Qwen3-4B-Instruct-2507", @"C:\models\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");
    private static readonly AssistantModelInfo Qwen17 =
        new("Qwen3-1.7B-Instruct", @"C:\models\q17.gguf", new string('b', 64), 32768, "Apache-2.0");

    private static AssistantManifestCache Cache(params AssistantModelInfo[] models)
        => new(_ => Task.FromResult(new AssistantModelManifest(models,
            models.FirstOrDefault(m => m.CanonicalName == AssistantModelManifest.DefaultCanonicalName)
                ?? models.FirstOrDefault(), [])));

    private static LoadedProjection Projection(IReadOnlyList<DisplayRow> rows, string versionId = "v1")
    {
        var started = new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
        return new LoadedProjection(
            new SessionRecord(), SessionMeta.CreateDefault(AppKind.Webex, started, self: null),
            [], null, null, new Dictionary<string, Matter>(), [], started, rows,
            new TranscriptHeader("t", "Webex", started, 0, "base.en", "CPU"),
            new SessionTextView("t", [], [], started, null, 0, "call", "", null),
            versionId);
    }

    private SummarizationService Make(FakeRunner runner, IReadOnlyList<DisplayRow> rows,
        AssistantGate? gate = null, AssistantManifestCache? cache = null, string versionId = "v1")
        => new(_paths, () => _settings, TimeProvider.System, runner, _store,
            gate ?? new AssistantGate(() => null, pollMs: 10), cache ?? Cache(Qwen4B, Qwen17),
            loadProjection: (_, _) => Task.FromResult(Projection(rows, versionId)));

    private static IReadOnlyList<DisplayRow> SmallRows() =>
    [
        new DisplayRow { DisplayName = "Sam", Text = "We agreed to file Tuesday.", StartMs = 0, EndMs = 2000 },
        new DisplayRow { DisplayName = "Client", Text = "Yes.", StartMs = 2500, EndMs = 2900 },
    ];

    private static IEnumerable<AssistantEvent> GoodScript(string text, string backend = "cuda")
        => [new AssistantChunk(text), new AssistantDone(backend, 100, 20)];

    /// <summary>Drives SummarizeAsync through a REAL AssistantJobRunner (not the script-based
    /// FakeRunner above) so the reverse-cancel test proves the cancel reaches all the way to a
    /// genuine process Kill() - mirrors AssistantJobRunnerTests.FakeProcess. Sends one chunk,
    /// then blocks on the caller's token so the job only unwinds when that token is cancelled.</summary>
    private sealed class BlockingProcess : IAssistantProcess
    {
        private bool _sentChunk;
        public bool Killed { get; private set; }
        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct) => Task.CompletedTask;

        public async Task<string?> ReadEventLineAsync(CancellationToken ct)
        {
            if (!_sentChunk)
            {
                _sentChunk = true;
                await Task.Yield();
                return "{\"type\":\"chunk\",\"text\":\"partial\"}";
            }
            await Task.Delay(Timeout.Infinite, ct);   // blocks until the job token is cancelled
            return null;
        }

        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFactory(BlockingProcess proc) : IAssistantProcessFactory
    {
        public Task<IAssistantProcess> StartAsync(CancellationToken ct) => Task.FromResult<IAssistantProcess>(proc);
    }

    [Fact]
    public async Task Single_call_summary_persists_with_full_provenance()
    {
        var runner = new FakeRunner(_ => GoodScript("## Summary\nFiled Tuesday."));
        var seen = new List<AssistantEvent>();
        var v = await Make(runner, SmallRows(), versionId: "v2-base.en-2026-07-13")
            .SummarizeAsync("s1", seen.Add, null, CancellationToken.None);

        var req = Assert.Single(runner.Requests);                       // fits -> ONE call
        Assert.Equal("summarize", req.Op);
        Assert.Equal(@"C:\models\q4b.gguf", req.ModelPath);             // locked default picked
        Assert.Equal(TokenBudget.MinCtxTokens, req.CtxTokens);          // tiny job -> ctx floor
        Assert.Contains("Speakers in this call: Sam, Client.", req.PayloadJson);   // roster rode in
        Assert.Contains(AssistantPrompts.GroundingLine, req.PayloadJson);

        Assert.Equal("s1", v.Id);
        Assert.Equal("v2-base.en-2026-07-13", v.SourceTranscriptVersion);   // ACTIVE version recorded
        Assert.Equal(new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"), v.Model);
        Assert.Equal(AssistantPrompts.PromptVersion, v.PromptVersion);
        Assert.False(v.Stale);
        Assert.Equal("## Summary\nFiled Tuesday.", v.ContentMarkdown);
        Assert.Contains(seen, e => e is AssistantChunk);                // streamed to the caller
        Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_settings_model_pick_is_honored()
    {
        _settings = new Settings { Assistant = new AssistantSetting { Model = "Qwen3-1.7B-Instruct" } };
        var runner = new FakeRunner(_ => GoodScript("## Summary\nok.", backend: "cpu"));
        var v = await Make(runner, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None);
        Assert.Equal(@"C:\models\q17.gguf", runner.Requests[0].ModelPath);
        Assert.Equal("cpu", v.Model.Backend);                           // ACTUAL backend from done
    }

    [Fact]
    public async Task Empty_output_and_error_events_throw_and_persist_nothing()
    {
        // Design 7.4: empty model output -> error surfaced, nothing persisted (never a blank artifact).
        var empty = new FakeRunner(_ => GoodScript("   \n"));
        await Assert.ThrowsAsync<AssistantException>(() =>
            Make(empty, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None));

        var err = new FakeRunner(_ => [new AssistantError("JOB_FAILED: oom")]);
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            Make(err, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None));
        Assert.Contains("oom", ex.Message);
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task No_installed_model_is_an_honest_error()
    {
        var runner = new FakeRunner(_ => GoodScript("x"));
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            Make(runner, SmallRows(), cache: Cache())
                .SummarizeAsync("s1", null, null, CancellationToken.None));
        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Long_sessions_map_reduce_and_persist_the_merged_result()
    {
        // ~120k chars of turns >> 80% of the 32k operating budget -> map-reduce (design 7.4).
        var rows = Enumerable.Range(0, 400).Select(i => new DisplayRow
        { DisplayName = "Sam", Text = new string('x', 300), StartMs = i * 1000, EndMs = i * 1000 + 900 })
            .ToList();
        int expectedChunks = SummarizationService.SplitIntoChunks(
            AssistantInputShaper.BuildTranscriptText(rows),
            TokenBudget.ChunkBudgetChars(SummarizationService.MapCtxTokens)).Count;
        Assert.True(expectedChunks > 1);

        var runner = new FakeRunner(req => GoodScript(
            req.PayloadJson.Contains("You are reading part") ? "- note" : "## Summary\nmerged."));
        var seen = new List<AssistantEvent>();
        var v = await Make(runner, rows).SummarizeAsync("s1", seen.Add, null, CancellationToken.None);

        Assert.Equal(expectedChunks + 1, runner.Requests.Count);        // N maps + 1 reduce
        Assert.All(runner.Requests.Take(expectedChunks),
            r => Assert.Equal(SummarizationService.MapCtxTokens, r.CtxTokens));
        Assert.Equal("## Summary\nmerged.", v.ContentMarkdown);
        Assert.Contains(seen, e => e is AssistantProgress { Phase: "map" });
        Assert.Contains(seen, e => e is AssistantProgress { Phase: "reduce" });
    }

    [Fact]
    public async Task Depth2_hierarchical_reduce_overflow_throws_honest_error_and_persists_nothing()
    {
        // Design 7.4: hierarchical reduce caps at MAX DEPTH 2 (TokenBudget.MaxReduceDepth).
        // Drive 8 map outputs of 20,000 chars each so the combined reduce overflows the 32k
        // operating budget (TokenBudget.MaxCtxTokens) at BOTH depth 1 and depth 2, forcing
        // genuine two-level batching that never collapses to a single reduce:
        //   depth 1: ChunkBudgetChars(MaxCtxTokens) = (32768*80/100 - 600)*2 = 51,228 chars per
        //            batch; at most 2 of our 20,000-char outputs fit per batch (3 = 60,000 >
        //            51,228) -> 8 outputs -> 4 batches (never 1 -> never collapses).
        //   depth 2: the 4 depth-1 batch outputs (also 20,000 chars, same stub) -> 2 batches
        //            by the same arithmetic (still never 1).
        //   after depth 2 the for-loop ends (MaxReduceDepth=2) and the honest too-long error
        //   fires - nothing is ever persisted.
        // Rows: 8 turns, each line ("Sam: " + 20,000 x's = 20,005 chars) is comfortably under
        // the MAP-split budget (ChunkBudgetChars(MapCtxTokens)=25,014) but two of them together
        // are not (40,011 > 25,014), so the map phase splits deterministically into exactly 8
        // chunks (one row per chunk).
        var rows = Enumerable.Range(0, 8).Select(i => new DisplayRow
        { DisplayName = "Sam", Text = new string('x', 20000), StartMs = i * 1000, EndMs = i * 1000 + 900 })
            .ToList();

        var runner = new FakeRunner(req => GoodScript(
            req.PayloadJson.Contains("You are reading part")
                ? new string('m', 20000)     // map output
                : new string('r', 20000)));  // reduce (batch) output, any depth

        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            Make(runner, rows).SummarizeAsync("s1", null, null, CancellationToken.None));

        Assert.Equal(
            "This session is too long for the configured model - the summary cannot be generated.",
            ex.Message);
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));   // nothing persisted, ever

        // Pin that both hierarchy levels actually ran (not a depth-1 short-circuit or an early
        // "batches.Count <= 1" break): 8 map calls, then 4 depth-1 batch reduces, then 2
        // depth-2 batch reduces = 14 requests total, 6 of them reduces.
        int reduceRequests = runner.Requests.Count(r => r.PayloadJson.Contains("merging per-part notes"));
        Assert.Equal(6, reduceRequests);
        Assert.Equal(8, runner.Requests.Count - reduceRequests);
        Assert.Equal(14, runner.Requests.Count);
    }

    [Fact]
    public async Task Queued_while_recording_then_runs_when_idle()
    {
        // Design 7.1/7.7: mid-recording -> visibly queued, never refused, never auto-cancelled.
        string? busy = "Waiting for the recording to finish...";
        var gate = new AssistantGate(() => busy, pollMs: 10);
        var runner = new FakeRunner(_ => GoodScript("## Summary\nok."));
        var waits = new List<string>();

        var job = Make(runner, SmallRows(), gate: gate)
            .SummarizeAsync("s1", null, waits.Add, CancellationToken.None);
        await Task.Delay(80);
        Assert.False(job.IsCompleted);
        busy = null;
        var v = await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Waiting for the recording to finish...", Assert.Single(waits.Distinct()));
        Assert.False(v.Stale);
    }

    [Fact]
    public async Task Recording_start_cancels_in_flight_summarize_and_persists_nothing()
    {
        // Design 7.1 reverse direction (whole-branch review fix): a recording START cancels an
        // in-flight summarize job so the assistant yields the engine to live transcription. The
        // cancel must reach the process boundary (Kill()) and must throw BEFORE any persist.
        var proc = new BlockingProcess();
        var runner = new AssistantJobRunner(new BlockingFactory(proc));
        var chunkSeen = new TaskCompletionSource();
        var service = new SummarizationService(_paths, () => _settings, TimeProvider.System, runner, _store,
            new AssistantGate(() => null, pollMs: 10), Cache(Qwen4B, Qwen17),
            loadProjection: (_, _) => Task.FromResult(Projection(SmallRows())));

        var job = service.SummarizeAsync("s1",
            evt => { if (evt is AssistantChunk) chunkSeen.TrySetResult(); },
            null, CancellationToken.None);
        await chunkSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        service.CancelForRecording();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => job.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));   // nothing persisted on cancel
        Assert.True(proc.Killed);                                            // reached the process boundary
    }

    [Fact]
    public async Task CancelForRecording_with_no_active_job_is_a_safe_no_op()
    {
        var service = Make(new FakeRunner(_ => GoodScript("x")), SmallRows());
        service.CancelForRecording();   // nothing running - must not throw
    }

    [Fact]
    public void SplitIntoChunks_respects_line_boundaries_and_hard_splits_oversize_lines()
    {
        var chunks = SummarizationService.SplitIntoChunks("aa\nbb\ncc\ndd", 6);
        Assert.Equal(new[] { "aa\nbb", "cc\ndd" }, chunks);
        var hard = SummarizationService.SplitIntoChunks(new string('z', 10), 4);
        Assert.Equal(new[] { "zzzz", "zzzz", "zz" }, hard);
    }
}
