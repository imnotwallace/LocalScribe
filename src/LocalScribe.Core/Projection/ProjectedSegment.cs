using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>A segment paired with its projected text (post vocabulary + edits), carrying the
/// original line so name resolution (which needs seq/source/speakerLabel) still works.</summary>
public sealed record ProjectedSegment(TranscriptLine Line, string Text)
{
    public int Seq => Line.Seq;
    public TranscriptSource Source => Line.Source;
    public long StartMs => Line.StartMs;
    public long EndMs => Line.EndMs;
}
