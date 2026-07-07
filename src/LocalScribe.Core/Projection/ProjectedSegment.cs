using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>A segment paired with its projected text (post vocabulary + edits), carrying the
/// original line so name resolution still works. For a split child (design §2.2) the text, times,
/// and speaker come from the split part while Line stays the untouched machine original — so
/// RawText and seq/source still resolve from Line.</summary>
public sealed record ProjectedSegment(TranscriptLine Line, string Text, bool Corrected = false,
    bool IsSplitChild = false, int PartIndex = 0,
    long? StartMsOverride = null, long? EndMsOverride = null,
    string? SpeakerParticipantId = null, string? SpeakerClusterKey = null)
{
    public int Seq => Line.Seq;
    public TranscriptSource Source => Line.Source;
    public long StartMs => StartMsOverride ?? Line.StartMs;
    public long EndMs => EndMsOverride ?? Line.EndMs;
}
