using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

public class TranscriptionWorkerTests
{
    private sealed class ScriptedFactory : IEngineFactory
    {
        public readonly List<(BackendPlan Plan, string? Language)> Created = new();
        private readonly Func<BackendPlan, ITranscriptionEngine> _make;
        public ScriptedFactory(Func<BackendPlan, ITranscriptionEngine> make) => _make = make;
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
        {
            Created.Add((plan, language));
            return Task.FromResult(_make(plan));
        }
    }

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
        var factory = new ScriptedFactory(_ => new FakeTranscriptionEngine("small.en",
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
        var factory = new ScriptedFactory(_ => new FakeTranscriptionEngine("small.en", _ => script.Dequeue()));
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
        var factory = new ScriptedFactory(plan => plan.ModelName == "small.en"
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
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
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

        Assert.Equal(Markers.TranscriptionLagging, Assert.Single(markers));  // once, not per segment
        Assert.Equal(2, factory.Created.Count);                              // downgraded engine
        Assert.Equal("base.en", factory.Created[1].Plan.ModelName);
    }

    [Fact]
    public async Task Language_lock_recreates_engine_with_locked_language()
    {
        var clock = new FakeClock();
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hallo", "de", 0.0)));
        var worker = Worker(factory, clock, lang: new LanguageResolver("auto", probeCount: 2));

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
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
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
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
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
    public async Task Language_lock_to_english_on_large_v3_does_not_append_nonexistent_en_suffix()
    {
        // Finding I2: large-v3 has no ".en" weights (ggml-large-v3.en.bin does not exist), so
        // the English weight fix-up must not fire for it - unlike tiny/base/small/medium.
        var clock = new FakeClock();
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
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
}
