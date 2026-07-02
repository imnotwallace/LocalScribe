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

    public void Dispose() => _sink.Dispose();
}
