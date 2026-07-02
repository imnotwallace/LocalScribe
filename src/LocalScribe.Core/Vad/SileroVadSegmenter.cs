using System.Runtime.CompilerServices;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Thin async wrapper over VadCore: pushes frames, yields finalized segments,
/// and force-flushes the in-progress utterance at end-of-stream (spec section 4 EOF flush).</summary>
public sealed class SileroVadSegmenter : IVadSegmenter
{
    private readonly SourceKind _source;
    private readonly VadOptions _options;
    private readonly ISpeechProbabilityModel _model;

    public SileroVadSegmenter(SourceKind source, VadOptions options, ISpeechProbabilityModel model)
        => (_source, _options, _model) = (source, options, model);

    public async IAsyncEnumerable<AudioSegment> SegmentAsync(
        IAsyncEnumerable<AudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        var core = new VadCore(_source, _options, _model);
        await foreach (var frame in frames.WithCancellation(ct))
            foreach (var seg in core.Push(frame))
                yield return seg;

        if (core.Flush() is { } residual)
            yield return residual;
    }
}
