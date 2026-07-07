namespace LocalScribe.Core.Projection;

/// <summary>One rendered row: a grouped speaker turn (IsMarker=false, DisplayName set) or a
/// standalone system marker (IsMarker=true, DisplayName null). Segments (Stage 6.1) lists the
/// turn's constituent transcript segments in display order - empty for markers and for rows built
/// without payloads (the live view); the file renderers ignore it.</summary>
public sealed record DisplayRow
{
    public bool IsMarker { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public string? DisplayName { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<RowSegment> Segments { get; init; } = [];

    public bool HasCorrection => Segments.Any(s => s.IsCorrected);
    public bool HasPin => Segments.Any(s => s.IsPinned);
}
