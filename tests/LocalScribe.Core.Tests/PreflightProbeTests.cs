using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class PreflightProbeTests
{
    private static float[] Frame(float value) => Enumerable.Repeat(value, 512).ToArray();

    [Fact]
    public async Task Returns_peak_of_observed_frames()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.1f), Frame(-0.6f), Frame(0.3f)]);
        float peak = await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(0.6f, peak, precision: 5);
    }

    [Fact]
    public async Task All_zeros_source_reports_zero_peak_below_threshold()
    {
        var source = new FakeCaptureSource(SourceKind.Remote, [Frame(0f), Frame(0f)]);
        float peak = await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(0f, peak);
        Assert.True(peak < PreflightProbe.SilencePeakThreshold);
    }

    [Fact]
    public async Task Does_not_dispose_the_source()
    {
        var inner = new FakeCaptureSource(SourceKind.Local, [Frame(0.2f)]);
        var wrapper = new DisposalTrackingSource(inner);
        await PreflightProbe.MeasurePeakAsync(wrapper, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.False(wrapper.Disposed);
    }

    private sealed class DisposalTrackingSource(ICaptureSource inner) : ICaptureSource
    {
        public bool Disposed;
        public SourceKind Source => inner.Source;
        public event Action<AudioFrame>? FrameAvailable
        { add { inner.FrameAvailable += value; } remove { inner.FrameAvailable -= value; } }
        public void Start() => inner.Start();
        public void Stop() => inner.Stop();
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }

    [Fact]
    public void StartPeakWindow_flags_a_leg_that_stays_below_the_silence_floor()
    {
        var w = new PreflightProbe.StartPeakWindow(graceMs: 1000);
        Assert.False(w.Feed(0f, 0));       // window opens at t=0
        Assert.False(w.Feed(0f, 500));     // still inside the grace window
        Assert.True(w.Feed(0f, 1000));     // window closes; peak never rose -> silent, flagged once
        Assert.False(w.Feed(0f, 1500));    // decided once; never re-fires
    }

    [Fact]
    public void StartPeakWindow_does_not_flag_a_leg_that_produced_real_audio()
    {
        var w = new PreflightProbe.StartPeakWindow(graceMs: 1000);
        Assert.False(w.Feed(0f, 0));
        Assert.False(w.Feed(0.3f, 400));   // speech-level peak inside the window
        Assert.False(w.Feed(0f, 1000));    // window closes but peak reached speech -> not silent
    }
}
