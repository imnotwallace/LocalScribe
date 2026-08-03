namespace LocalScribe.Core.Projection;

/// <summary>One cadence chunk of a grouped turn (design 2026-08-02 item 5): the stamp shown at the
/// chunk's head, the chunk's text, and its constituent segments. Chunk 0 renders as the normal
/// turn; chunks 1..n render as stamp-only continuation paragraphs (the name is not repeated).</summary>
public sealed record CadenceChunk(long StampMs, string Text, IReadOnlyList<RowSegment> Segments);

/// <summary>Splits a grouped DisplayRow into export chunks at segment boundaries. Two
/// independent triggers, whichever fires first (design 2026-08-03 section 8):
/// intervalMs of wall time since the last shown stamp (the 2026-08-02 item 5 cadence, still
/// behind the export dialog's checkbox), or maxChars of accumulated text (ALWAYS on - it is
/// what guarantees a (cont'd) label near the top of nearly every page, which is a correctness
/// property, not a preference). A row still passes through as ONE whole-row chunk when BOTH
/// triggers are off, the row is a marker, the row has no Segments payload (live rows, legacy
/// fixtures), or no boundary crosses either threshold. The whole-row chunk carries row.Text
/// VERBATIM - never the Segments re-join - so uncadenced output stays byte-identical
/// (SectionGrouper's null-payload merge means Segments-derived text is not guaranteed to equal
/// row.Text). Split chunk text uses the single-space join byte-identical to
/// SectionGrouper.cs:34.</summary>
public static class TimestampCadence
{
    public static IReadOnlyList<CadenceChunk> Chunk(DisplayRow row, int intervalMs, int maxChars)
    {
        if ((intervalMs <= 0 && maxChars <= 0) || row.IsMarker || row.Segments.Count == 0)
            return [WholeRow(row)];

        var chunks = new List<CadenceChunk>();
        var current = new List<RowSegment>();
        long lastStampMs = row.StartMs;
        long chunkStampMs = row.StartMs;
        int chars = 0;
        foreach (var seg in row.Segments)
        {
            bool byTime = intervalMs > 0 && seg.StartMs - lastStampMs >= intervalMs;
            bool byLength = maxChars > 0 && chars > 0 && chars + seg.ProjectedText.Length > maxChars;
            if (current.Count > 0 && (byTime || byLength))
            {
                chunks.Add(Close(chunkStampMs, current));
                current = [];
                chunkStampMs = seg.StartMs;
                lastStampMs = seg.StartMs;
                chars = 0;
            }
            current.Add(seg);
            chars += seg.ProjectedText.Length;
        }
        chunks.Add(Close(chunkStampMs, current));
        return chunks.Count == 1 ? [WholeRow(row)] : chunks;
    }

    private static CadenceChunk WholeRow(DisplayRow row) => new(row.StartMs, row.Text, row.Segments);

    private static CadenceChunk Close(long stampMs, List<RowSegment> segments)
        => new(stampMs, string.Join(" ", segments.Select(s => s.ProjectedText)), segments);
}
