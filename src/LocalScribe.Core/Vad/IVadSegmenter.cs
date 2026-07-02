using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Design seam: an async frame stream in, finalized utterances out (spec section 4).</summary>
public interface IVadSegmenter
{
    IAsyncEnumerable<AudioSegment> SegmentAsync(IAsyncEnumerable<AudioFrame> frames, CancellationToken ct);
}
