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
        pipeline.StartLeg(source, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Single(output);
        Assert.Equal(SourceKind.Local, output[0].Audio.Source);
    }

    [Fact]
    public async Task Stop_flushes_the_in_progress_utterance()
    {
        // Speech right up to the stop - no trailing silence. The EOF flush (user decision
        // 2026-07-02: never drop trailing audio on Stop/Pause) must still emit it.
        var (worker, output, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Remote, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter: null);

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Remote, SpeechThenSilence(6, 0)), cts.Token);
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

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token);
        await pipeline.StopLegAndFlushAsync();
        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token);
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
        pipeline.StartLeg(idle, cts.Token);
        Assert.Throws<InvalidOperationException>(
            () => pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, []), cts.Token));
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
