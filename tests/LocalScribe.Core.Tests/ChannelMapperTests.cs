using LocalScribe.Core.Audio;
using LocalScribe.Core.Import;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class ChannelMapperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-chanmap-" + Guid.NewGuid().ToString("N"));
    public ChannelMapperTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Tone (300 Hz, 0.5 amplitude) only on the channels named in toneChannels; the rest silent.
    private string WriteWav(string name, int rate, int channels, int ms, params int[] toneChannels)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        int frames = rate * ms / 1000;
        var buf = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            foreach (int ch in toneChannels)
                buf[f * channels + ch] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    // Canonical PCM WAV whose data-chunk size field CLAIMS `declaredDataBytes` but only
    // `actualDataBytes` of (zero) PCM physically follow - a "lying header": NAudio bounds reads by
    // the header claim and stops at physical EOF, so the legs silently come up short.
    private string WriteLyingHeaderWav(string name, int rate, int channels,
        int declaredDataBytes, int actualDataBytes)
    {
        string path = Path.Combine(_root, name);
        const short bits = 16;
        short blockAlign = (short)(channels * bits / 8);
        int byteRate = rate * blockAlign;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + declaredDataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(byteRate); w.Write(blockAlign); w.Write(bits);
        w.Write("data"u8.ToArray()); w.Write(declaredDataBytes);   // THE LIE: claims more than follows
        w.Write(new byte[actualDataBytes]);                        // fewer bytes physically present
        return path;
    }

    private static float PeakOf(string wavPath)
    {
        using var r = new AudioFileReader(wavPath);
        float peak = 0;
        var buf = new float[16000];
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
        return peak;
    }

    [Fact]
    public void Plan_covers_every_channel_and_answer_combination()
    {
        var mono = ChannelMapper.Plan(1, StereoMapping.Split);       // decode truth wins over the answer
        Assert.Equal([new LegPlan(SourceKind.Local, null)], mono.Legs);
        Assert.False(mono.DownmixedMultichannel);

        var split = ChannelMapper.Plan(2, StereoMapping.Split);
        Assert.Equal([new LegPlan(SourceKind.Local, 0), new LegPlan(SourceKind.Remote, 1)], split.Legs);

        var swapped = ChannelMapper.Plan(2, StereoMapping.SplitSwapped);
        Assert.Equal([new LegPlan(SourceKind.Local, 1), new LegPlan(SourceKind.Remote, 0)], swapped.Legs);

        var downmix = ChannelMapper.Plan(2, StereoMapping.Downmix);
        Assert.Equal([new LegPlan(SourceKind.Local, null)], downmix.Legs);
        Assert.False(downmix.DownmixedMultichannel);

        var surround = ChannelMapper.Plan(6, StereoMapping.Downmix);
        Assert.Equal([new LegPlan(SourceKind.Local, null)], surround.Legs);
        Assert.True(surround.DownmixedMultichannel);                 // drives the "with a note" marker
    }

    [Fact]
    public void WriteLegs_split_keeps_each_party_on_its_own_leg()
    {
        string stereo = WriteWav("stereo.wav", 16000, 2, 500, 0);    // tone LEFT only
        var legs = ChannelMapper.WriteLegs(stereo,
            ChannelMapper.Plan(2, StereoMapping.Split), _root, CancellationToken.None);

        Assert.Equal(2, legs.Count);
        string local = legs.Single(l => l.Kind == SourceKind.Local).WavPath;
        string remote = legs.Single(l => l.Kind == SourceKind.Remote).WavPath;
        Assert.True(PeakOf(local) > 0.3f, "left (tone) must land on the Local leg");
        Assert.True(PeakOf(remote) < 0.01f, "right (silence) must land on the Remote leg");

        var swappedLegs = ChannelMapper.WriteLegs(stereo,
            ChannelMapper.Plan(2, StereoMapping.SplitSwapped),
            Directory.CreateDirectory(Path.Combine(_root, "sw")).FullName, CancellationToken.None);
        Assert.True(PeakOf(swappedLegs.Single(l => l.Kind == SourceKind.Remote).WavPath) > 0.3f);
        Assert.True(PeakOf(swappedLegs.Single(l => l.Kind == SourceKind.Local).WavPath) < 0.01f);
    }

    [Fact]
    public void WriteLegs_rejects_a_lying_wav_header_instead_of_silently_truncating()
    {
        // Header claims 1 s (32000 bytes) of 16 kHz mono 16-bit PCM; only 0.5 s (16000) follows.
        // A native-WAV import's decoded "truth" IS this header (design 4.2), so the duration gate
        // can't catch it - WriteLegs must, or half the evidence vanishes with no trace.
        string lying = WriteLyingHeaderWav("lying.wav", 16000, 1,
            declaredDataBytes: 32000, actualDataBytes: 16000);
        var ex = Assert.Throws<InvalidDataException>(() => ChannelMapper.WriteLegs(lying,
            ChannelMapper.Plan(1, StereoMapping.Downmix), _root, CancellationToken.None));
        Assert.Contains("truncat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteLegs_resamples_to_16k_and_downmixes_multichannel()
    {
        string surround = WriteWav("four.wav", 44100, 4, 1000, 0, 1, 2, 3);
        var legs = ChannelMapper.WriteLegs(surround,
            ChannelMapper.Plan(4, StereoMapping.Downmix), _root, CancellationToken.None);

        var leg = Assert.Single(legs);
        Assert.Equal(SourceKind.Local, leg.Kind);
        using var r = new WaveFileReader(leg.WavPath);
        Assert.Equal(16000, r.WaveFormat.SampleRate);                // resampled
        Assert.Equal(1, r.WaveFormat.Channels);                      // mono
        Assert.InRange(r.TotalTime.TotalMilliseconds, 900, 1100);    // ~1 s survives the resample
        Assert.True(PeakOf(leg.WavPath) > 0.3f);                     // averaged energy present
    }
}
