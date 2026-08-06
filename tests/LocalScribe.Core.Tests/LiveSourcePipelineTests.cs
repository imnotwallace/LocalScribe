using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class LiveSourcePipelineTests
{
    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static float[][] SpeechThenSilence(int speechFrames, int silenceFrames)
    {
        var frames = new List<float[]>();
        for (int i = 0; i < speechFrames; i++) frames.Add(Enumerable.Repeat(0.5f, 512).ToArray());
        for (int i = 0; i < silenceFrames; i++) frames.Add(new float[512]);
        return frames.ToArray();
    }

    private static (TranscriptionWorker Worker, List<TranscribedSegment> Out, Task Loop, CancellationTokenSource Cts)
        StartWorker()
    {
        var worker = new TranscriptionWorker(new FakeEngineFactory(),
            new BackendPlan(Backend.Cpu, "tiny.en"), new LanguageResolver("en"),
            new FakeClock(), new TranscriptionWorkerOptions());
        var output = new List<TranscribedSegment>();
        worker.SegmentTranscribed += ts => { lock (output) output.Add(ts); };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (worker, output, worker.RunAsync(cts.Token), cts);
    }

    [Fact]
    public async Task Leg_feeds_vad_segments_into_the_worker()
    {
        var (worker, output, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter: null);

        var source = new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3));
        pipeline.StartLeg(source, cts.Token, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Single(output);
        Assert.Equal(SourceKind.Local, output[0].Audio.Source);
    }

    [Fact]
    public async Task Audio_keeps_writing_after_the_feed_token_is_cancelled()
    {
        // Simulate a worker fault: cancel ONLY the feed token mid-leg. The audio writer must keep
        // receiving frames (evidentiary audio survives a transcriber failure - design section 3).
        var (worker, _, loop, cts) = StartWorker();
        long written = 0;
        var sink = new DelegateSink(mem => written += mem.Length);
        var audioWriter = new AlignedAudioWriter(sink);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter);

        using var captureCts = new CancellationTokenSource();
        using var feedCts = new CancellationTokenSource();
        // 20 speech frames then 20 silence: plenty of frames to observe writes after cancelling feed.
        var source = new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(20, 20));
        pipeline.StartLeg(source, captureCts.Token, feedCts.Token);

        feedCts.Cancel();                       // "worker died" - stop feeding VAD, keep audio
        await pipeline.StopLegAndFlushAsync();  // graceful stop drains the capture loop

        worker.Complete();
        await loop;
        Assert.True(written > 0, "audio writer received no frames after the feed was cancelled");
    }

    [Fact]
    public async Task A_manual_source_can_emit_frames_after_StartLeg_returns()
    {
        // Guards the new double itself (Tier 1B design 2026-08-05, T1-4). FakeCaptureSource replays
        // everything synchronously inside Start() and can never emit again, which is precisely why
        // no existing test can express "frames stopped arriving". If this ever regresses, every
        // capture-health test silently becomes vacuous.
        var (worker, _, loop, cts) = StartWorker();
        long written = 0;
        var sink = new DelegateSink(mem => written += mem.Length);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, new AlignedAudioWriter(sink));

        var source = new ManualCaptureSource(SourceKind.Local);
        pipeline.StartLeg(source, cts.Token, cts.Token);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(0, written);                                  // nothing emitted yet

        source.Emit(startMs: 0);
        source.Emit(startMs: 32);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.True(written >= 1024);                              // both frames reached the writer
        Assert.Equal(1, source.StopCount);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task An_audio_write_fault_halts_the_bridge_and_reports_the_leg_once()
    {
        // Disk full mid-recording. Before Tier 1B this faulted _audioLoop silently: the fault was
        // observed only when StopLegAndFlushAsync awaited it (possibly an hour later), and in the
        // meantime the capture callback kept writing into the frame bridge's UNBOUNDED channel with
        // no reader left - memory growth on top of an already-failing recording.
        var (worker, _, loop, cts) = StartWorker();
        var boom = new IOException("There is not enough space on the disk.");
        var sink = new DelegateSink(_ => throw boom);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, new AlignedAudioWriter(sink));

        var faults = new TaskCompletionSource<(SourceKind Kind, Exception Ex)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.LegFaulted += (k, ex) => faults.TrySetResult((k, ex));

        var source = new ManualCaptureSource(SourceKind.Local);
        pipeline.StartLeg(source, cts.Token, cts.Token);
        source.Emit(startMs: 0);

        var reported = await faults.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SourceKind.Local, reported.Kind);
        Assert.Same(boom, reported.Ex);

        // The bridge was COMPLETED by the continuation, which detaches FrameAvailable - so a frame
        // emitted after the fault reaches nothing at all and the channel cannot grow.
        source.Emit(startMs: 32);

        // The fault is NOT swallowed: Stop still surfaces it, unchanged, so StopAsync's existing
        // leg-fault handling (no pad, teardown, rethrow) behaves exactly as before.
        var thrown = await Assert.ThrowsAsync<IOException>(() => pipeline.StopLegAndFlushAsync());
        Assert.Same(boom, thrown);

        worker.Complete();
        await loop;
    }

    [Fact]
    public async Task Stop_flushes_the_in_progress_utterance()
    {
        // Speech right up to the stop - no trailing silence. The EOF flush (user decision
        // 2026-07-02: never drop trailing audio on Stop/Pause) must still emit it.
        var (worker, output, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Remote, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter: null);

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Remote, SpeechThenSilence(6, 0)), cts.Token, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Single(output);
    }

    [Fact]
    public async Task Two_legs_produce_two_segments_and_tap_writes_audio()
    {
        var (worker, output, loop, cts) = StartWorker();
        var sinkSamples = new List<float>();
        var sink = new DelegateSink(s => sinkSamples.AddRange(s.ToArray()));
        using var audio = new AlignedAudioWriter(sink);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, audio);
        float lastPeak = 0f;
        pipeline.PeakObserved += (_, p) => lastPeak = Math.Max(lastPeak, p);

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Equal(2, output.Count);
        Assert.True(sinkSamples.Count >= 2 * 7 * 512);   // both legs' frames written
        Assert.Equal(0.5f, lastPeak);
    }

    [Fact]
    public async Task StartLeg_while_running_throws()
    {
        var (worker, _, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, null);
        var idle = new IdleCaptureSource(SourceKind.Local);    // never emits, never completes
        pipeline.StartLeg(idle, cts.Token, cts.Token);
        Assert.Throws<InvalidOperationException>(
            () => pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, []), cts.Token, cts.Token));
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;
    }

    [Fact]
    public async Task StopLegAndFlush_when_no_leg_is_noop()
    {
        var (worker, _, loop, _) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, null);
        await pipeline.StopLegAndFlushAsync();               // must not throw
        worker.Complete();
        await loop;
    }

    private sealed class DelegateSink(Action<ReadOnlyMemory<float>> onWrite) : IAudioFileSink
    {
        public void Write(ReadOnlySpan<float> mono16k) => onWrite(mono16k.ToArray());
        public void Dispose() { }
    }

    private sealed class IdleCaptureSource(SourceKind source) : ICaptureSource
    {
        public SourceKind Source => source;
        public event Action<AudioFrame>? FrameAvailable { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
