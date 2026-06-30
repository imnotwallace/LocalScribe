// tests/LocalScribe.Core.Tests/WavSinkTests.cs
using LocalScribe.Core.Audio;
using NAudio.Wave;
using Xunit;

public class WavSinkTests
{
    [Fact]
    public void Writes_16k_mono_pcm_and_roundtrips_samples()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}.wav");
        float[] samples = { 0f, 0.25f, -0.25f, 0.5f, -0.5f };
        try
        {
            using (var sink = new WavSink(path)) sink.Write(samples);

            using var reader = new AudioFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);

            var buf = new float[samples.Length];
            int read = reader.Read(buf, 0, buf.Length);
            Assert.Equal(samples.Length, read);
            for (int i = 0; i < samples.Length; i++)
                Assert.Equal(samples[i], buf[i], 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
