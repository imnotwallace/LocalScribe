namespace LocalScribe.Core.Projection;

/// <summary>One cadence chunk of a grouped turn (design 2026-08-02 item 5): the stamp shown at the
/// chunk's head, the chunk's text, and its constituent segments. Chunk 0 renders as the normal
/// turn; chunks 1..n render as stamp-only continuation paragraphs (the name is not repeated).</summary>
public sealed record CadenceChunk(long StampMs, string Text, IReadOnlyList<RowSegment> Segments);

/// <summary>Splits a grouped DisplayRow into export chunks at the segment boundaries where at
/// least intervalMs of wall time has elapsed since the LAST SHOWN stamp (design 2026-08-02
/// item 5). Pure and export-only: transcript.jsonl, the read view, and the save-time projections
/// never see chunks. A row passes through as ONE whole-row chunk when intervalMs is not positive,
/// the row is a marker, the row has no Segments payload (live rows, legacy test fixtures), or no
/// boundary crosses the interval. The whole-row chunk carries row.Text VERBATIM - never the
/// Segments re-join - so uncadenced output stays byte-identical (SectionGrouper's null-payload
/// merge means Segments-derived text is not guaranteed to equal row.Text). Split chunk text uses
/// the single-space join byte-identical to SectionGrouper.cs:34.</summary>
public static class TimestampCadence
{
    public static IReadOnlyList<CadenceChunk> Chunk(DisplayRow row, int intervalMs)
    {
        if (intervalMs <= 0 || row.IsMarker || row.Segments.Count == 0)
            return [WholeRow(row)];

        var chunks = new List<CadenceChunk>();
        var current = new List<RowSegment>();
        long lastStampMs = row.StartMs;
        long chunkStampMs = row.StartMs;
        foreach (var seg in row.Segments)
        {
            if (current.Count > 0 && seg.StartMs - lastStampMs >= intervalMs)
            {
                chunks.Add(Close(chunkStampMs, current));
                current = [];
                chunkStampMs = seg.StartMs;
                lastStampMs = seg.StartMs;
            }
            current.Add(seg);
        }
        chunks.Add(Close(chunkStampMs, current));
        return chunks.Count == 1 ? [WholeRow(row)] : chunks;
    }

    private static CadenceChunk WholeRow(DisplayRow row) => new(row.StartMs, row.Text, row.Segments);

    private static CadenceChunk Close(long stampMs, List<RowSegment> segments)
        => new(stampMs, string.Join(" ", segments.Select(s => s.ProjectedText)), segments);
}
