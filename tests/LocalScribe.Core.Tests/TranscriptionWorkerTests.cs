using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Tests;
using LocalScribe.Core.Transcription;

public class TranscriptionWorkerTests
{
    // FakeEngineFactory (shared with LiveSourcePipelineTests) lives in LiveTestDoubles.cs -
    // promoted from this file's former private ScriptedFactory to avoid two near-duplicate
    // IEngineFactory fakes.

    private static AudioSegment Seg(long startMs = 0, int ms = 1000) =>
        new(SourceKind.Local, startMs, startMs + ms, new float[16 * ms]);

    private static TranscriptionWorker Worker(
        IEngineFactory factory, FakeClock clock, TranscriptionWorkerOptions? o = null,
        LanguageResolver? lang = null) =>
        new(factory, new BackendPlan(Backend.Cpu, "small.en"),
            lang ?? new LanguageResolver("en"), clock, o ?? new TranscriptionWorkerOptions());

    [Fact]
    public async Task Transcribes_and_fires_kept_segments_in_order()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(_ => new FakeTranscriptionEngine("small.en",
            s => new TranscriptionResult($"seg@{s.StartMs}", "en", 0.01)));
        var worker = Worker(factory, clock);
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        await worker.EnqueueAsync(Seg(1000), default);
        worker.Complete();
        await run;

        Assert.Equal(new[] { "seg@0", "seg@1000" }, got.Select(g => g.Result.Text));
    }

    [Fact]
    public async Task High_no_speech_prob_and_empty_text_are_dropped()
    {
        var clock = new FakeClock();
        var script = new Queue<TranscriptionResult>(new[]
        {
            new TranscriptionResult("", "en", 0.0),            // empty -> dropped
            new TranscriptionResult("Thank you.", "en", 0.95), // hallucination -> dropped
            new TranscriptionResult("real words", "en", 0.05), // kept
        });
        var factory = new FakeEngineFactory(_ => new FakeTranscriptionEngine("small.en", _ => script.Dequeue()));
        var worker = Worker(factory, clock);
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal("real words", Assert.Single(got).Result.Text);
    }

    [Fact]
    public async Task Vram_oom_downgrades_one_step_and_retries_same_segment()
    {
        var clock = new FakeClock();
        var errors = new List<string>();
        var factory = new FakeEngineFactory(plan => plan.ModelName == "small.en"
            ? new FakeTranscriptionEngine("small.en", new object[]
                { new VramOutOfMemoryException("oom") })
            : new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult("recovered", "en", 0.0)));
        var worker = Worker(factory, clock);
        worker.ErrorRaised += errors.Add;
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Contains("VRAM_OOM", errors);
        var only = Assert.Single(got);
        Assert.Equal("recovered", only.Result.Text);
        Assert.Equal("base.en", only.ModelName);               // one ladder step down
        Assert.Equal(2, factory.Created.Count);                // recreated once
    }

    [Fact]
    public async Task Sustained_rtf_over_one_raises_lagging_marker_once_and_downgrades()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);      // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var markers = new List<string>();
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        worker.MarkerRaised += markers.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 6; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        // Lagging fires ONCE (not per segment); the downgrade's engine swap additionally
        // writes the weights-changed marker (small.en file -> base.en file) - a model
        // downgrade IS a weights change and is traced as one (review finding 2026-07-13).
        Assert.Equal(Markers.TranscriptionLagging, Assert.Single(markers, m => m == Markers.TranscriptionLagging));
        Assert.Equal(
            string.Format(Markers.TranscriptionWeightsChanged, "ggml-small.en.bin", "ggml-base.en.bin"),
            Assert.Single(markers, m => m != Markers.TranscriptionLagging));
        Assert.Equal(2, factory.Created.Count);                              // downgraded engine
        Assert.Equal("base.en", factory.Created[1].Plan.ModelName);
    }

    [Fact]
    public async Task Language_lock_recreates_engine_with_locked_language()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hallo", "de", 0.0)));
        // Multilingual starting model, not the shared Worker() helper's "small.en" default:
        // post Guard-1 an ".en"-producing engine's detections are untrusted junk and would
        // never be observed, so this probe-then-lock scenario needs a trustworthy source.
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cpu, "small"),
            new LanguageResolver("auto", probeCount: 2), clock, new TranscriptionWorkerOptions());

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);
        Assert.Null(factory.Created[0].Language);              // probing: detection on
        Assert.Equal("de", factory.Created[1].Language);       // locked
    }

    [Fact]
    public async Task Rtf_downgrade_segment_keeps_old_model_provenance()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);      // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 6; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        // The 3rd segment (index 2) is the one whose RTF window trips the downgrade, but it
        // was still transcribed BY the pre-downgrade engine ("small.en") - provenance must
        // reflect the model that actually produced the text, not the model swapped in after.
        Assert.Equal("small.en", got[2].ModelName);
        Assert.Equal("base.en", got[3].ModelName);             // later segment: new (downgraded) model
    }

    [Fact]
    public async Task Language_lock_to_non_english_strips_en_suffix_from_model()
    {
        var clock = new FakeClock();
        // The fake engine reports itself as multilingual ("small") regardless of what the plan
        // says, so Guard 1 still trusts its detections even while the plan itself starts as
        // "small.en" - isolating the mechanical strip-suffix fix-up (this method's subject)
        // from Guard 1's separate junk-source gating (covered by
        // English_only_model_detected_language_is_ignored above, where plan and producing
        // engine agree on ".en").
        var factory = new FakeEngineFactory(_ => new FakeTranscriptionEngine("small",
            s => new TranscriptionResult("hallo", "de", 0.0)));
        var worker = Worker(factory, clock, lang: new LanguageResolver("auto", probeCount: 1));

        var run = worker.RunAsync(default);
        for (int i = 0; i < 2; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal("de", factory.Created[1].Language);
        Assert.Equal("small", factory.Created[1].Plan.ModelName);   // multilingual weights, no ".en"
    }

    [Fact]
    public async Task English_only_model_detected_language_is_ignored()
    {
        // An English-only model has no multilingual head - its DetectedLanguage field is junk
        // (observed live: "az" on clean English speech with tiny.en). The resolver must never
        // be fed that junk, or it locks a bogus language and forces a weight swap to a model
        // that was never fetched (only .en weights are downloaded by tools/fetch-models.ps1).
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hello", "az", 0.0)));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cpu, "tiny.en"),
            new LanguageResolver("auto", probeCount: 2), clock, new TranscriptionWorkerOptions());
        var errors = new List<string>();
        worker.ErrorRaised += errors.Add;
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 4; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(4, got.Count);
        Assert.All(got, g => Assert.Equal("tiny.en", g.ModelName));
        Assert.Single(factory.Created);                 // never recreated - language never locked
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Missing_weights_on_language_lock_falls_back_instead_of_crashing()
    {
        // The language-lock weight swap is an optimization, never worth a dead session: if the
        // target weight file is missing (e.g. only .en models were fetched), the worker must
        // revert the plan, raise MODEL_DOWNLOAD_FAILED, and keep transcribing on the current
        // (still-working) engine instead of letting the swap's exception fault the whole loop.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.ModelName == "tiny.en"
            ? throw new FileNotFoundException("ggml-tiny.en.bin")
            : new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult("hello", "en", 0.0)));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cpu, "tiny"),
            new LanguageResolver("auto", probeCount: 1), clock, new TranscriptionWorkerOptions());
        var errors = new List<string>();
        worker.ErrorRaised += errors.Add;
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Contains("MODEL_DOWNLOAD_FAILED", errors);
        Assert.Equal(3, got.Count);
        Assert.All(got, g => Assert.Equal("tiny", g.ModelName));   // fell back, kept transcribing
    }

    [Fact]
    public async Task Swap_failure_other_than_missing_file_also_falls_back()
    {
        // The language-lock swap is an optimization, never worth a dead live session: any
        // creation failure other than a missing weight file - notably a VRAM OOM, made MORE
        // likely by create-before-dispose holding two engines at once - must also revert the
        // plan, raise BACKEND_INIT_FAILED, and keep transcribing on the current engine instead
        // of propagating and killing the worker loop.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.ModelName == "tiny.en"
            ? throw new VramOutOfMemoryException("oom during language-lock swap")
            : new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult("hello", "en", 0.0)));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cpu, "tiny"),
            new LanguageResolver("auto", probeCount: 1), clock, new TranscriptionWorkerOptions());
        var errors = new List<string>();
        worker.ErrorRaised += errors.Add;
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Contains("BACKEND_INIT_FAILED", errors);
        Assert.Equal(3, got.Count);
        Assert.All(got, g => Assert.Equal("tiny", g.ModelName));   // fell back, kept transcribing
    }

    [Fact]
    public async Task Language_lock_to_english_on_large_v3_does_not_append_nonexistent_en_suffix()
    {
        // Finding I2: large-v3 has no ".en" weights (ggml-large-v3.en.bin does not exist), so
        // the English weight fix-up must not fire for it - unlike tiny/base/small/medium.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hello", "en", 0.0)));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cpu, "large-v3"),
            new LanguageResolver("auto", probeCount: 1), clock, new TranscriptionWorkerOptions());

        var run = worker.RunAsync(default);
        for (int i = 0; i < 2; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal("en", factory.Created[1].Language);
        Assert.Equal("large-v3", factory.Created[1].Plan.ModelName);   // unchanged - no ".en" appended
    }

    // ---------- Weights-file provenance (review findings 2026-07-13) ----------
    // The model FILE is resolved per backend at engine creation, so an engine recreation can
    // silently swap weights (e.g. CUDA f16 -> CPU q8_0 on the VRAM-OOM floor fall). Evidentiary
    // invariant: any weights change mid-session writes a transcript marker, and every segment
    // carries the file that produced it.

    [Fact]
    public async Task Floor_fall_preserves_cpu_threads_in_the_recreated_plan()
    {
        // Pins the plan `with { Backend = Cpu }` carry at the worker itself, not just via
        // record semantics: a mutant that rebuilt the plan without CpuThreads must fail here.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.Backend == Backend.Cuda
            ? new FakeTranscriptionEngine("tiny.en", new object[] { new VramOutOfMemoryException("oom") })
            : new FakeTranscriptionEngine("tiny.en", s => new TranscriptionResult("recovered", "en", 0.0)));
        var worker = new TranscriptionWorker(factory,
            new BackendPlan(Backend.Cuda, "tiny.en", CpuThreads: 6),
            new LanguageResolver("en"), clock, new TranscriptionWorkerOptions());

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);
        var floored = factory.Created[1].Plan;
        Assert.Equal(Backend.Cpu, floored.Backend);      // tiny.en is the ladder floor
        Assert.Equal(6, floored.CpuThreads);             // carried through the `with` flip
        Assert.Equal(6, floored.EffectiveThreads);       // and active on the CPU backend
    }

    [Fact]
    public async Task Weights_file_change_on_engine_recreation_raises_exactly_one_marker()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.Backend == Backend.Cuda
            ? new FakeTranscriptionEngine("tiny.en", new object[] { new VramOutOfMemoryException("oom") },
                weightsFile: "ggml-tiny.en.bin")
            : new FakeTranscriptionEngine("tiny.en", s => new TranscriptionResult("recovered", "en", 0.0),
                weightsFile: "ggml-tiny.en-q8_0.bin"));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cuda, "tiny.en"),
            new LanguageResolver("en"), clock, new TranscriptionWorkerOptions());
        var markers = new List<string>();
        worker.MarkerRaised += markers.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        // Initial creation is silent; the floor-fall recreation changed the file -> one marker.
        Assert.Equal(
            string.Format(Markers.TranscriptionWeightsChanged, "ggml-tiny.en.bin", "ggml-tiny.en-q8_0.bin"),
            Assert.Single(markers));
    }

    [Fact]
    public async Task Recreation_that_keeps_the_same_weights_file_raises_no_weights_marker()
    {
        // Compare by FILE, not by recreation event: master's floor fall reloaded byte-identical
        // weights and was rightly silent - that behavior must survive.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.ModelName == "small.en"
            ? new FakeTranscriptionEngine("small.en", new object[] { new VramOutOfMemoryException("oom") },
                weightsFile: "ggml-shared.bin")
            : new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult("recovered", "en", 0.0), weightsFile: "ggml-shared.bin"));
        var worker = Worker(factory, clock);
        var markers = new List<string>();
        worker.MarkerRaised += markers.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);          // downgrade did recreate
        Assert.Empty(markers);                           // same file -> silent
    }

    // Marker POSITION (re-verify probe 2026-07-13): the weights marker must sit on the correct
    // side of the segments around it, or the transcript misattributes a segment to the wrong
    // file - the exact defect the marker exists to prevent. The worker defers a pending marker
    // and flushes it before segments the NEW weights produced, after segments the OLD produced.

    [Fact]
    public async Task Weights_marker_follows_the_trigger_segment_the_old_weights_produced()
    {
        // Lagging path: the downgrade fires mid-iteration, BEFORE the trigger segment is
        // emitted - but that segment was produced by the OLD weights, so the marker must land
        // after it (and before the first new-weights segment).
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);      // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var events = new List<string>();
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        worker.MarkerRaised += m => events.Add(m.StartsWith("transcription weights") ? "weights-marker" : "marker");
        worker.SegmentTranscribed += ts => events.Add($"seg@{ts.Audio.StartMs}:{ts.WeightsFile}");

        var run = worker.RunAsync(default);
        for (int i = 0; i < 4; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        int marker = events.IndexOf("weights-marker");
        int trigger = events.IndexOf("seg@2000:ggml-small.en.bin");   // produced by OLD weights
        int firstNew = events.IndexOf("seg@3000:ggml-base.en.bin");   // produced by NEW weights
        Assert.True(trigger >= 0 && marker >= 0 && firstNew >= 0, string.Join(" | ", events));
        Assert.True(trigger < marker, $"marker must follow the old-weights trigger: {string.Join(" | ", events)}");
        Assert.True(marker < firstNew, $"marker must precede the first new-weights segment: {string.Join(" | ", events)}");
    }

    [Fact]
    public async Task Weights_marker_precedes_the_retried_segment_the_new_weights_produced()
    {
        // OOM path: the failed attempt produced nothing; the retried segment comes from the
        // NEW weights, so the marker lands before it.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => plan.Backend == Backend.Cuda
            ? new FakeTranscriptionEngine("tiny.en", new object[] { new VramOutOfMemoryException("oom") },
                weightsFile: "ggml-tiny.en.bin")
            : new FakeTranscriptionEngine("tiny.en", s => new TranscriptionResult("recovered", "en", 0.0),
                weightsFile: "ggml-tiny.en-q8_0.bin"));
        var worker = new TranscriptionWorker(factory, new BackendPlan(Backend.Cuda, "tiny.en"),
            new LanguageResolver("en"), clock, new TranscriptionWorkerOptions());
        var events = new List<string>();
        worker.MarkerRaised += m => events.Add("weights-marker");
        worker.SegmentTranscribed += ts => events.Add($"seg:{ts.WeightsFile}");

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Equal(new[] { "weights-marker", "seg:ggml-tiny.en-q8_0.bin" }, events);
    }

    [Fact]
    public async Task Pending_weights_marker_still_flushes_when_no_segment_follows()
    {
        // Lagging downgrade on the LAST segment: nothing follows to flush against, so the
        // pending marker must flush at completion - the weights change is evidence even if
        // the new engine never produced a segment.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var events = new List<string>();
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        worker.MarkerRaised += m => events.Add(m.StartsWith("transcription weights") ? "weights-marker" : "marker");
        worker.SegmentTranscribed += ts => events.Add($"seg@{ts.Audio.StartMs}");

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);   // trigger IS the last
        worker.Complete();
        await run;

        Assert.Equal("weights-marker", events.Last());
        Assert.True(events.IndexOf("seg@2000") < events.IndexOf("weights-marker"), string.Join(" | ", events));
    }

    [Fact]
    public async Task Segments_carry_the_weights_file_that_produced_them()
    {
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hello", "en", 0.0), weightsFile: "ggml-small.en-q8_0.bin"));
        var worker = Worker(factory, clock);
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Equal("ggml-small.en-q8_0.bin", Assert.Single(got).WeightsFile);
    }

    [Fact]
    public async Task RecentRtf_reports_the_rolling_window_average()
    {
        // Design 2026-07-13 section 5 item 4 (keep-up chip): the worker's EXISTING per-segment RTF
        // window is the lag data - expose its mean read-only, no new telemetry. FakeClock deltas
        // are exact, so the expected averages are exact doubles (no tolerance needed).
        var clock = new FakeClock();
        var script = new Queue<long>(new long[] { 2, 1 });     // per-segment clock multiplier
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += script.Dequeue() * (s.EndMs - s.StartMs);   // RTF 2.0 then 1.0
            return new TranscriptionResult("ok", "en", 0.0);
        }));
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        Assert.Null(worker.RecentRtf);                          // nothing tracked yet

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);             // RTF 2.0
        await worker.EnqueueAsync(Seg(1000), default);          // RTF 1.0
        worker.Complete();
        await run;

        Assert.Equal(1.5, worker.RecentRtf);                    // (2.0 + 1.0) / 2; window (3) never tripped
    }

    [Fact]
    public async Task RecentRtf_resets_when_the_lagging_downgrade_clears_the_window()
    {
        // The one-shot RTF_LAGGING downgrade clears _rtfWindow (existing behavior, untouched).
        // RecentRtf must reset with it: the pre-downgrade engine's stale >1.0 average must not
        // keep the keep-up chip red after the downgrade already replaced that engine.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);       // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);   // 3rd trips the one-shot
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);                 // the downgrade really fired
        Assert.Null(worker.RecentRtf);                          // window cleared -> sentinel, not a stale 2.0
    }
}
