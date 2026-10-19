namespace LocalScribe.Core.Projection;

/// <summary>A flat, ordered pre-render row (design 5.4 section 4.2): one projected segment or one
/// system marker, before section grouping. EndMs carries the segment's VAD end so SectionGrouper
/// can measure the same-speaker silence gap. Public so SectionGrouper and the live view consume it.
/// Segment (Stage 6.1) is the optional per-segment identity payload accumulated into
/// DisplayRow.Segments; null (the live view, markers) leaves the row's Segments empty.
///
/// A human-authored row (design 2026-08-11 section 2.3) takes TranscriptProjection.InsertedRank,
/// Seq 0, and EndMs == StartMs; its identity lives on Segment.InsertionId, which is also the
/// sort's fourth and final tiebreak. Seq is a placeholder on such a row and is only ever compared
/// against another inserted row's, because no machine row shares InsertedRank.</summary>
public sealed record PreRow(
    long StartMs, long EndMs, int SourceRank, int Seq, string? Name, string Text, bool IsMarker,
    RowSegment? Segment = null);
