using LocalScribe.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class FlacToWavTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ls-flac2wav-" + Guid.NewGuid().ToString("N"));
    public FlacToWavTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // The fix's core property: decoding our (seektable-less) FLAC to linear-PCM WAV preserves the
    // samples 1:1, in order, with NO length drift - so byte-offset in the WAV maps linearly to time
    // and Media Foundation positions/seeks it exactly (unlike the FLAC, where MF estimates and drifts).
    [Fact]
    public void Decodes_flac_to_wav_sample_accurately_preserving_length_and_content()
    {
        // A non-repeating swept tone: any sample offset or drift shows up as a large per-sample error.
        int n = 16000 * 3;                                   // 3 s @ 16 kHz mono
        var signal = new float[n];
        for (int i = 0; i < n; i++)
            signal[i] = 0.5f * MathF.Sin(2f * MathF.PI * (200f + i * 0.02f) * i / 16000f);

        string flac = Path.Combine(_dir, "in.flac");
        using (var sink = new FlacAudioSink(flac)) sink.Write(signal);

        string wav = Path.Combine(_dir, "out.wav");
        FlacToWav.Convert(flac, wav);

        using var reader = new AudioFileReader(wav);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);

        var read = new List<float>();
        var buf = new float[16000];
        int r;
        while ((r = reader.Read(buf, 0, buf.Length)) > 0) read.AddRange(buf.AsSpan(0, r).ToArray());

        Assert.Equal(n, read.Count);                         // no length drift - exact sample count
        for (int i = 0; i < n; i++)
            Assert.True(MathF.Abs(read[i] - signal[i]) < 2e-3f,
                $"sample {i} drifted: {read[i]} vs {signal[i]}");   // only int16 quantization differs
    }
}
