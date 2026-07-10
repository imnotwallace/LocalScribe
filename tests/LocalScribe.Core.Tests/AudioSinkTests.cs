// tests/LocalScribe.Core.Tests/AudioSinkTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

public class AudioSinkTests
{
    private static float[] Sine(int seconds)
    {
        var pcm = new float[16000 * seconds];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
        return pcm;
    }

    [Fact]
    public void Flac_sink_writes_a_flac_stream_smaller_than_wav()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string flac = Path.Combine(dir, "local.flac");
            string wav = Path.Combine(dir, "local.wav");
            var pcm = Sine(2);

            using (var f = AudioSinkFactory.Create(flac, AudioFormat.Flac)) f.Write(pcm);
            using (var w = AudioSinkFactory.Create(wav, AudioFormat.Wav)) w.Write(pcm);

            byte[] head = new byte[4];
            using (var fs = File.OpenRead(flac)) fs.ReadExactly(head);
            Assert.Equal("fLaC"u8.ToArray(), head);                       // FLAC magic
            Assert.True(new FileInfo(flac).Length < new FileInfo(wav).Length,
                "FLAC of a tonal signal must compress below WAV");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Flac_sink_writes_no_nonzero_padding_metadata_block()
    {
        // Root cause (on-box probe, 2026-07-11): Windows Media Foundation reports a constant
        // per-file forward clock offset after any seek+Play on our FLAC files, sized ~=
        // metadataBytes / avgAudioByteRate. The offending bytes are an 8192-byte PADDING
        // metadata block that CUETools.Codecs.FLAKE's FlakeWriter emits by default
        // (FlakeWriter.Padding == 8192). FlacAudioSink must set Padding = 0 so the FLAC
        // stream has no (or a zero-size) PADDING block. LocalScribe never rewrites FLAC
        // metadata after finalize, so removing padding is safe.
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string flac = Path.Combine(dir, "nopad.flac");
            using (var sink = new FlacAudioSink(flac))
                sink.Write(Sine(1));

            byte[] file = File.ReadAllBytes(flac);
            Assert.Equal("fLaC"u8.ToArray(), file[..4]);
            Assert.Equal(0, file[4] & 0x7F);   // spec: the FIRST metadata block must be
                                                // STREAMINFO (type 0) - pins structural validity

            int pos = 4;
            bool sawNonzeroPadding = false;
            while (pos + 4 <= file.Length)
            {
                byte header0 = file[pos];
                bool isLast = (header0 & 0x80) != 0;
                int type = header0 & 0x7F;
                int size = (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
                if (type == 1 && size > 0) sawNonzeroPadding = true;   // type 1 == PADDING
                pos += 4 + size;
                if (isLast) break;
            }

            Assert.False(sawNonzeroPadding,
                "FlacAudioSink must not emit a nonzero-size PADDING metadata block " +
                "(it shifts MF's post-seek clock readback on this box's Media Foundation)");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Wav_sink_roundtrips_through_existing_wavsink_format()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string wav = Path.Combine(dir, "x.wav");
            using (var w = AudioSinkFactory.Create(wav, AudioFormat.Wav)) w.Write(Sine(1));
            using var reader = new NAudio.Wave.WaveFileReader(wav);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        }
        finally { Directory.Delete(dir, true); }
    }
}
