using LocalScribe.Core.Audio;
using NAudio.Wave;
namespace LocalScribe.Core.Pipeline;

/// <summary>Offline frame source: WAV file -> 16 kHz mono 512-sample AudioFrames with
/// sample-counted StartMs. Mirrors what the live capture emits, so the same VAD/worker/merger
/// pipeline runs unchanged (design "walking skeleton").</summary>
public static class WavFileFrameReader
{
    private const int FrameSamples = 512;

    public static IEnumerable<AudioFrame> ReadFrames(string wavPath, SourceKind source)
    {
        using var reader = new AudioFileReader(wavPath);           // float samples
        int channels = reader.WaveFormat.Channels;
        if (channels > 2)
            throw new NotSupportedException($"{channels}-channel WAV is not supported (mono/stereo only).");
        int rate = reader.WaveFormat.SampleRate;
        var resampler = rate == 16000 ? null : new MonoResampler16k(rate);

        var pending = new List<float>();
        var readBuf = new float[rate * channels];                  // ~1 s per read
        long emitted = 0;
        int n;
        while ((n = reader.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            float[] mono = channels == 2
                ? PcmConverter.StereoToMono(readBuf.AsSpan(0, n))
                : readBuf.AsSpan(0, n).ToArray();
            pending.AddRange(resampler is null ? mono : resampler.Process(mono));

            while (pending.Count >= FrameSamples)
            {
                var frame = pending.GetRange(0, FrameSamples).ToArray();
                pending.RemoveRange(0, FrameSamples);
                yield return new AudioFrame(source, emitted * 1000 / 16000, frame);
                emitted += FrameSamples;
            }
        }
        // trailing partial window (< 32 ms) intentionally dropped
    }
}
