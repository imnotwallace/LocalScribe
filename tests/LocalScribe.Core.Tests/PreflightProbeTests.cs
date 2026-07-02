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
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.2f)]);
        await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        source.Start();                       // still usable: no ObjectDisposedException
    }
}
