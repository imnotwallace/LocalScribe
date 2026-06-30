// tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class MonoResampler16kTests
{
    [Fact]
    public void Downsamples_48k_to_16k_by_one_third_length()
    {
        var r = new MonoResampler16k(inputSampleRate: 48000);
        var input = new float[48000];            // 1 second @ 48k
        for (int i = 0; i < input.Length; i++)   // 440 Hz sine, harmless content
            input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 48000f) * 0.5f;

        float[] outp = r.Process(input);

        Assert.InRange(outp.Length, 15840, 16160);   // ~16000 (+/-1% for filter edge effects)
    }
}
