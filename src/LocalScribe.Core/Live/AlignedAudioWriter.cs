using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Live;

/// <summary>Writes retained audio keeping the file sample-aligned to the session clock:
/// a frame stamped at StartMs always begins at sample StartMs * rate / 1000, with silence
/// padding for any gap (Pause stops capture but the clock keeps ticking - spec 2.1). This is
/// what lets Stage-5 diarisation seek the retained file by transcript startMs/endMs. Frames
/// arriving slightly early (ms-level capture jitter) are appended as-is; sub-frame drift is
/// accepted rather than resampled.</summary>
public sealed class AlignedAudioWriter : IDisposable
{
    private static readonly float[] SilenceChunk = new float[1600];   // 100 ms @ 16 kHz
    private readonly IAudioFileSink _sink;
    private readonly int _sampleRate;

    public long SamplesWritten { get; private set; }

    public AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000)
        => (_sink, _sampleRate) = (sink, sampleRate);

    public void Write(AudioFrame frame)
    {
        long expectedStart = frame.StartMs * _sampleRate / 1000;
        long gap = expectedStart - SamplesWritten;
        while (gap > 0)
        {
            int chunk = (int)Math.Min(gap, SilenceChunk.Length);
            _sink.Write(SilenceChunk.AsSpan(0, chunk));
            SamplesWritten += chunk;
            gap -= chunk;
        }
        _sink.Write(frame.Samples);
        SamplesWritten += frame.Samples.Length;
    }

    /// <summary>Stage 5.4 Phase 3 (write-side fix): pad the retained file with silence up to the
    /// session clock, so retained audio always spans the full session (observed: ~23.6 s audio vs
    /// 25.3 s session clock because the last frame precedes Stop). STRICTLY additive: appends zeros
    /// after the last recorded sample, never seeks, never rewrites; a target at or behind
    /// SamplesWritten is a no-op. Same ms-to-sample arithmetic as Write's expectedStart.</summary>
    public void PadToMs(long endMs)
    {
        long target = endMs * _sampleRate / 1000;
        long gap = target - SamplesWritten;
        while (gap > 0)
        {
            int chunk = (int)Math.Min(gap, SilenceChunk.Length);
            _sink.Write(SilenceChunk.AsSpan(0, chunk));
            SamplesWritten += chunk;
            gap -= chunk;
        }
    }

    public void Dispose() => _sink.Dispose();
}
