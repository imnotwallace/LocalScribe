// tests/LocalScribe.Core.Tests/PcmConverterTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class PcmConverterTests
{
    [Fact]
    public void Int16BytesToFloat_maps_full_scale()
    {
        byte[] bytes = { 0x00, 0x00, 0xFF, 0x7F };   // 0x0000 -> 0.0 ; 0x7FFF -> ~+1.0 (LE)
        float[] f = PcmConverter.Int16BytesToFloat(bytes);
        Assert.Equal(2, f.Length);
        Assert.Equal(0f, f[0], 5);
        Assert.True(f[1] > 0.99f && f[1] <= 1.0f);
    }

    [Fact]
    public void StereoToMono_averages_channels()
    {
        float[] interleaved = { 1.0f, 0.0f,  0.0f, 1.0f };   // L,R, L,R
        float[] mono = PcmConverter.StereoToMono(interleaved);
        Assert.Equal(new[] { 0.5f, 0.5f }, mono);
    }

    [Fact]
    public void FloatToInt16Bytes_roundtrips_within_tolerance()
    {
        float[] original = { 0f, 0.5f, -0.5f, 0.999f };
        byte[] bytes = PcmConverter.FloatToInt16Bytes(original);
        float[] back = PcmConverter.Int16BytesToFloat(bytes);
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], back[i], 3);
    }
}
