using System.Globalization;
using System.Linq;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Metadata strings shared by session.txt and BOTH export renderers (design 2026-08-03).
/// Extracted from SessionTextRenderer so the three surfaces cannot drift: the exports previously
/// printed a start time only while session.txt printed start-end-duration. Invariant-culture by
/// construction, like every other exported string.</summary>
public static class MetadataFormat
{
    /// <summary>"2026-06-30 14:32 - 15:09 (37 min)", or the start-only form when the session has
    /// no end (a live/unfinalized session exported mid-recording, design 2026-08-03 section 11).</summary>
    public static string DateLine(SessionTextView v)
    {
        long durationMin = (long)Math.Round(v.DurationMs / 60000.0);
        return v.EndedAtLocal is { } end
            ? string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} - {end:HH:mm} ({durationMin} min)")
            : string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} ({durationMin} min)");
    }

    /// <summary>Who actually speaks in the rows, distinct, in first-appearance order (design
    /// 2026-08-03 section 6). Deliberately distinct from SessionTextView.Participants, which is
    /// user-curated metadata and may name people who never speak (or omit people who do).</summary>
    public static string SpeakersHeard(IReadOnlyList<DisplayRow> rows)
    {
        var seen = new List<string>();
        foreach (var row in rows)
            if (!row.IsMarker && !string.IsNullOrEmpty(row.DisplayName)
                && !seen.Contains(row.DisplayName, StringComparer.Ordinal))
                seen.Add(row.DisplayName);
        return string.Join(", ", seen);
    }

    /// <summary>Pick the disclaimer variant from the human-layer counts (design 2026-08-11
    /// section 3.3). SuppressedDuplicates is DELIBERATELY excluded: PhantomBleedDedup is a machine
    /// act, and a document whose only non-zero count is that one was reviewed by nobody. Claiming
    /// otherwise in a document served on the other side is a false statement about who touched the
    /// evidence - the Omitted: line discloses the dedup instead. Null counts (every all-default
    /// ExportProvenance) read as untouched, which keeps pre-feature output unchanged.</summary>
    public static string DisclaimerFor(HumanLayerCounts? counts)
        => counts is { } c
           && (c.Corrections > 0 || c.Splits > 0 || c.SpeakerPins > 0 || c.SpeakerNames > 0)
            ? ExportNotices.DisclaimerEdited
            : ExportNotices.Disclaimer;

    /// <summary>"4 duplicate turns (echo)", or null when nothing was suppressed (design
    /// 2026-08-11 section 3.3). The VALUE half only - each renderer supplies the "Omitted" label
    /// itself. Null rather than "" so a renderer's `is { }` pattern omits the whole line rather
    /// than printing an empty label.
    ///
    /// This is the one disclosure Part 3 KEPT off the disclaimer: PhantomBleedDedup is a machine
    /// omission, so "reviewed and corrected by a person" would not cover it, and the spec calls
    /// its absence concealment. Says "turns", not "segments" (the unit SuppressedDuplicates
    /// actually counts): the owner chose the reader's word over the internal one, and a reader of
    /// an exported document has never heard of a segment.</summary>
    public static string? OmittedLine(HumanLayerCounts? counts)
        => counts is { SuppressedDuplicates: > 0 } c
            ? Count(c.SuppressedDuplicates, "duplicate turn (echo)", "duplicate turns (echo)")
            : null;

    private static string Count(int n, string one, string many)
        => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");
}
