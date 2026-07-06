namespace LocalScribe.Core.Projection;

/// <summary>A flat, ordered pre-render row (design 5.4 section 4.2): one projected segment or one
/// system marker, before section grouping. EndMs carries the segment's VAD end so SectionGrouper
/// can measure the same-speaker silence gap. Public so SectionGrouper and the live view consume it.</summary>
public sealed record PreRow(
    long StartMs, long EndMs, int SourceRank, int Seq, string? Name, string Text, bool IsMarker);
