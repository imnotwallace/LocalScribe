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
}
