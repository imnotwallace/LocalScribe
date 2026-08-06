using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>Writes retained audio keeping the file sample-aligned to the session clock:
/// a frame stamped at StartMs always begins at sample StartMs * rate / 1000, with silence
/// padding for any gap (Pause stops capture but the clock keeps ticking - spec 2.1). This is
/// what lets Stage-5 diarisation seek the retained file by transcript startMs/endMs. Frames
/// arriving slightly early (ms-level capture jitter) are appended as-is; sub-frame drift is
/// accepted rather than resampled.
///
/// Tier 1 T1-7 (spec 2026-08-05 :148-153): every zero this class inserts is now RECORDED in
/// FabricatedSilence, because the integrity manifest hashes the resulting file and a hash that
/// seals fabricated silence as original audio is worse than no hash at all.</summary>
public sealed class AlignedAudioWriter : IDisposable
{
    private static readonly float[] SilenceChunk = new float[1600];   // 100 ms @ 16 kHz
    private readonly IAudioFileSink _sink;
    private readonly int _sampleRate;
    private readonly List<FabricatedSpan> _fabricated = new();

    public long SamplesWritten { get; private set; }

    /// <summary>Which leg this writer owns. Self-identifying so PersistFinalAsync can key the
    /// fabricated-silence map by source without depending on the order of Session.AudioWriters.</summary>
    public SourceKind Source { get; }

    public int SampleRate => _sampleRate;

    /// <summary>Every run of machine-generated samples in this file, in write order (Tier 1 T1-7).
    /// Appended only on a transition, never per silence chunk: one 2-second dropout is ONE span,
    /// not twenty. Both writers below sit on the synchronous capture path, so this must stay
    /// allocation-light - a gapless session never allocates a span at all.</summary>
    public IReadOnlyList<FabricatedSpan> FabricatedSilence => _fabricated;

    /// <summary>source is TRAILING-OPTIONAL (house idiom for adding a seam without touching
    /// existing call sites): eleven existing tests construct this with just a sink.</summary>
    public AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000,
        SourceKind source = SourceKind.Local)
        => (_sink, _sampleRate, Source) = (sink, sampleRate, source);

    public void Write(AudioFrame frame)
    {
        long expectedStart = frame.StartMs * _sampleRate / 1000;
        long gap = expectedStart - SamplesWritten;
        if (gap > 0) Fill(gap, "clock-gap");
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
        long gap = endMs * _sampleRate / 1000 - SamplesWritten;
        if (gap > 0) Fill(gap, "end-pad");
    }

    /// <summary>Writes `samples` zeros in SilenceChunk-sized pieces and records the whole run as
    /// ONE span. Coalescing happens here, around the loop, rather than per chunk (Tier 1 T1-7).</summary>
    private void Fill(long samples, string reason)
    {
        long start = SamplesWritten;
        while (samples > 0)
        {
            int chunk = (int)Math.Min(samples, SilenceChunk.Length);
            _sink.Write(SilenceChunk.AsSpan(0, chunk));
            SamplesWritten += chunk;
            samples -= chunk;
        }
        _fabricated.Add(new FabricatedSpan
        { StartSample = start, EndSample = SamplesWritten, Reason = reason });
    }

    public void Dispose() => _sink.Dispose();
}
