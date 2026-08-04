using System.Linq;
namespace LocalScribe.Core.Projection;

/// <summary>Whole-row overlap selection for a time-range excerpt (design 2026-08-04 section 8).
/// A row is IN when it overlaps the range; rows are NEVER truncated - Text passes through
/// untouched - so the exported span snaps OUTWARD to turn boundaries. That is why the document
/// must report ActualSpan, not the requested range: reporting the request over outward-snapped
/// content would be a small lie in an evidentiary document.</summary>
public static class ExcerptSelector
{
    /// <summary>Half-open overlap [FromMs, ToMs). Zero-length rows - markers, which have
    /// StartMs == EndMs - are treated as POINTS, because a strict overlap test would drop every
    /// marker in the range.</summary>
    public static bool Covers(DisplayRow row, ExcerptRange range)
        => row.EndMs > row.StartMs
            ? row.StartMs < range.ToMs && row.EndMs > range.FromMs
            : row.StartMs >= range.FromMs && row.StartMs < range.ToMs;

    public static IReadOnlyList<DisplayRow> Select(IReadOnlyList<DisplayRow> rows, ExcerptRange range)
        => [.. rows.Where(r => Covers(r, range))];

    /// <summary>The span the SELECTED rows actually cover - what the document reports.</summary>
    public static (long FromMs, long ToMs) ActualSpan(IReadOnlyList<DisplayRow> selected)
        => selected.Count == 0
            ? (0L, 0L)
            : (selected.Min(r => r.StartMs), selected.Max(r => r.EndMs));
}
