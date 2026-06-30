// tests/LocalScribe.Core.Tests/SilenceGapFillerTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class SilenceGapFillerTests
{
    [Fact]
    public void No_gap_when_device_position_matches_written()
    {
        Assert.Equal(0, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 16000, devicePosFrames: 16000));
    }

    [Fact]
    public void Gap_is_device_position_minus_written()
    {
        // Target went silent for 0.5 s (8000 frames @ 16 kHz): device advanced, we did not write.
        Assert.Equal(8000, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 16000, devicePosFrames: 24000));
    }

    [Fact]
    public void Negative_drift_is_clamped_to_zero()
    {
        // Device position behind written count (jitter/overlap) -> never insert negative silence.
        Assert.Equal(0, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 24000, devicePosFrames: 16000));
    }

    [Fact]
    public void SilenceFrame_returns_zeros_of_requested_length()
    {
        float[] s = SilenceGapFiller.SilenceFrame(3);
        Assert.Equal(3, s.Length);
        Assert.All(s, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void SilenceFrame_of_nonpositive_length_is_empty()
    {
        Assert.Empty(SilenceGapFiller.SilenceFrame(0));
        Assert.Empty(SilenceGapFiller.SilenceFrame(-5));
    }
}
