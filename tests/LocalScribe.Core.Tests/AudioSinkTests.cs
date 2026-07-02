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
