using LocalScribe.Core.Import;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class FfmpegAudioDecoderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-ffdec-" + Guid.NewGuid().ToString("N"));
    public FfmpegAudioDecoderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WriteStereoWav(int rate, int ms)
    {
        string path = Path.Combine(_root, "in.wav");
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        int frames = rate * ms / 1000;
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
            buf[f * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));   // left only
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    [Fact]
    public async Task Wav_probe_is_native_and_reports_claims_plus_file_timestamps()
    {
        string wav = WriteStereoWav(44100, 500);
        var decoder = new FfmpegAudioDecoder(toolsDir: null);       // no FFmpeg needed for WAV

        var probe = await decoder.ProbeAsync(wav, CancellationToken.None);

        Assert.Equal("wav", probe.FormatName);
        Assert.Equal(2, probe.ClaimedChannels);
        Assert.Equal(44100, probe.ClaimedSampleRate);
        Assert.InRange(probe.ClaimedDurationMs!.Value, 480, 520);
        Assert.Equal(new FileInfo(wav).Length, probe.FileSizeBytes);
        Assert.Null(probe.MediaCreatedUtc);                         // WAV carries no creation tag
        Assert.NotNull(probe.FileCreatedUtc);                       // fallback timestamps present
        Assert.NotNull(probe.FileModifiedUtc);
    }

    [Fact]
    public async Task Wav_decode_is_native_and_reads_truth_from_the_stream()
    {
        string wav = WriteStereoWav(44100, 500);
        var decoder = new FfmpegAudioDecoder(toolsDir: null);

        var decoded = await decoder.DecodeAsync(wav, _root, CancellationToken.None);

        Assert.Equal(wav, decoded.PcmWavPath);                      // read in place, never modified
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        Assert.InRange(decoded.DurationMs, 480, 520);
    }

    [Fact]
    public async Task NonWav_without_ffmpeg_fails_with_the_fetch_instruction()
    {
        string mp3 = Path.Combine(_root, "in.mp3");
        await File.WriteAllBytesAsync(mp3, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        var decoder = new FfmpegAudioDecoder(toolsDir: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => decoder.ProbeAsync(mp3, CancellationToken.None));
        Assert.Contains("fetch-ffmpeg.ps1", ex.Message);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => decoder.DecodeAsync(mp3, _root, CancellationToken.None));
        Assert.Contains("LOCALSCRIBE_FFMPEG", ex2.Message);
    }

    [Fact]
    public async Task Missing_file_fails_fast()
    {
        var decoder = new FfmpegAudioDecoder(toolsDir: null);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => decoder.ProbeAsync(Path.Combine(_root, "absent.wav"), CancellationToken.None));
    }
}
