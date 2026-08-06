using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class FlacPcmReaderTests
{
    /// <summary>A FLAC whose "fLaC" magic is intact but whose metadata-block chain runs past the
    /// end of the file must RETURN, not spin (Tier 1B, 2026-08-06). MEASURED against FLAKE 1.0.5:
    /// FlakeReader's metadata parse loop does not bounds-check the declared block length against
    /// the stream, so at EOF it reads zero bytes forever and burns a core - 304 CPU-seconds before
    /// the run was killed. Files with NO magic, and empty files, already fail fast; a torn write
    /// that got the magic down is the case that hangs.
    ///
    /// This is a live product hazard, not just a test one: DurationMs is the re-transcription
    /// progress denominator, and Tier 1B makes launch-time recovery probe every crashed session's
    /// legs - i.e. exactly the files a torn write leaves behind. The timeout IS the assertion; a
    /// regression here would otherwise hang the suite instead of failing it.</summary>
    [Theory]
    [InlineData(8)]      // magic + 4 bytes of a block header
    [InlineData(41)]     // one byte short of a complete STREAMINFO block
    [InlineData(42)]     // exactly magic + block header + 34 - still short of this encoder's chain
    public void DurationMs_returns_promptly_for_a_truncated_flac(int keepBytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string whole = Path.Combine(dir, "whole.flac");
            using (var sink = AudioSinkFactory.Create(whole, LocalScribe.Core.Model.AudioFormat.Flac))
                sink.Write(new float[5 * 16000]);

            string torn = Path.Combine(dir, "torn.flac");
            byte[] all = File.ReadAllBytes(whole);
            File.WriteAllBytes(torn, all.AsSpan(0, Math.Min(keepBytes, all.Length)).ToArray());

            long value = -1;
            var probe = Task.Run(() => { value = FlacPcmReader.DurationMs(torn); });

            Assert.True(probe.Wait(TimeSpan.FromSeconds(10)),
                $"DurationMs did not return within 10 s for a FLAC truncated to {keepBytes} bytes - "
                + "the FLAKE metadata loop is spinning at EOF again.");
            Assert.Equal(0, value);          // 0 means UNKNOWN duration, the documented contract
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The other side of the guard above, and the reason it tests chain COMPLETENESS
    /// rather than a file-size floor: a file truncated mid-FRAME still has a whole metadata chain,
    /// so its STREAMINFO duration is readable and must keep being read. MEASURED: the same 5 s leg
    /// cut to 60 bytes reports 5000 ms, because FLAC records total samples in the header rather
    /// than deriving it from the frames present.</summary>
    [Fact]
    public void DurationMs_still_reads_the_header_of_a_file_truncated_after_its_metadata()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string whole = Path.Combine(dir, "whole.flac");
            using (var sink = AudioSinkFactory.Create(whole, LocalScribe.Core.Model.AudioFormat.Flac))
                sink.Write(new float[5 * 16000]);
            Assert.Equal(5000, FlacPcmReader.DurationMs(whole));

            string torn = Path.Combine(dir, "torn.flac");
            byte[] all = File.ReadAllBytes(whole);
            File.WriteAllBytes(torn, all.AsSpan(0, 60).ToArray());

            Assert.Equal(5000, FlacPcmReader.DurationMs(torn));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

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
