using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class FlacPcmReaderTests
{
    [Fact]
    public void Decodes_flac_written_by_FlacAudioSink_within_pcm_tolerance()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "remote.flac");
        try
        {
            // 8000 samples (0.5s @ 16k) of a low-amplitude ramp/sine, mono.
            var samples = new float[8000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = 0.25f * MathF.Sin(i * 0.05f);

            using (var sink = new FlacAudioSink(path))
                sink.Write(samples);          // FLAC is lossless for 16-bit PCM

            float[] decoded = FlacPcmReader.ReadMono16k(path);

            Assert.Equal(samples.Length, decoded.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.True(Math.Abs(samples[i] - decoded[i]) < 1.0f / 32768f + 1e-6f,
                    $"sample {i}: {samples[i]} vs {decoded[i]}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Rejects_non_16k_or_multichannel()
    {
        // A WAV at the wrong rate must throw InvalidDataException (guard for foreign files).
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "bad.wav");
        try
        {
            WriteWavHeaderOnly(path, sampleRate: 44100, channels: 2);
            Assert.Throws<InvalidDataException>(() => FlacPcmReader.ReadMono16k(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void WriteWavHeaderOnly(string path, int sampleRate, short channels)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(0);
    }
}
