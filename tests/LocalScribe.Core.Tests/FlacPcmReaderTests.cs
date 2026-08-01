using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class FlacPcmReaderTests
{
    [Fact]
    public void Rejects_non_16_bit_flac()
    {
        // A 24-bit FLAC is 16 kHz mono (passes the rate/channel guard) but 3 bytes/sample.
        // Without a bit-depth guard the decode loop feeds those bytes to Int16BytesToFloat
        // (2 bytes/sample), silently producing garbage - the latent bug from the 2026-07-31
        // smoke. It must throw a clear "16-bit" InvalidDataException instead, matching
        // FlacToWav's guard, so the helper's BAD_AUDIO filter reports it honestly.
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "wide.flac");
        try
        {
            Write24BitMono16kFlac(path, sampleCount: 8000);
            var ex = Assert.Throws<InvalidDataException>(() => FlacPcmReader.ReadMono16k(path));
            Assert.Contains("16-bit", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Writes a genuine 24-bit/mono/16 kHz FLAC (STREAMINFO reports bitsPerSample=24) via the
    // same Flake encoder FlacAudioSink uses, so the reader's format guard - which reads PCM
    // straight from STREAMINFO - is exercised without any external tool.
    private static void Write24BitMono16kFlac(string path, int sampleCount)
    {
        var cfg = new AudioPCMConfig(24, 1, 16000);
        using var writer = new FlakeWriter(path, cfg) { Padding = 0 };
        var bytes = new byte[sampleCount * 3];
        for (int i = 0; i < sampleCount; i++)
        {
            int v = (int)(4096.0 * Math.Sin(i * 0.05));   // small 24-bit signed sample
            bytes[i * 3 + 0] = (byte)(v & 0xFF);
            bytes[i * 3 + 1] = (byte)((v >> 8) & 0xFF);
            bytes[i * 3 + 2] = (byte)((v >> 16) & 0xFF);
        }
        var buffer = new AudioBuffer(cfg, sampleCount);
        buffer.Prepare(bytes, sampleCount);
        writer.Write(buffer);
    }

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

    [Fact]
    public void Corrupt_flac_bytes_surface_as_InvalidDataException()
    {
        // Garbage/truncated bytes: not a valid FLAC stream at all. FlakeReader's internal
        // decode can throw IOException/EndOfStreamException/other CUETools-internal types
        // for this - the helper must normalize ALL genuine decode failures to
        // InvalidDataException so Program.cs's BAD_AUDIO filter catches them.
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "corrupt.flac");
        try
        {
            var garbage = new byte[100];
            new Random(12345).NextBytes(garbage);
            File.WriteAllBytes(path, garbage);

            Assert.Throws<InvalidDataException>(() => FlacPcmReader.ReadMono16k(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Missing_flac_file_still_throws_FileNotFoundException()
    {
        // The directory must exist so the missing-FILE case is isolated from
        // DirectoryNotFoundException (a sibling of FileNotFoundException, not a subtype).
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "does-not-exist.flac");
        try
        {
            Assert.Throws<FileNotFoundException>(() => FlacPcmReader.ReadMono16k(path));
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
