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
    /// through its own metadata-line helper, the ExportedLine precedent. Null rather than "" so a
    /// renderer's `is { }` pattern omits the whole line rather than printing an empty label.
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

    /// <summary>"v2 \u00B7 large-v3-turbo \u00B7 cuda". Rendered for originals too -
    /// ShortId("v1") returns "v1", so no special-casing (design 2026-08-03 section 6).</summary>
    public static string VersionLine(ExportProvenance p)
        => string.Join(" \u00B7 ",
            new[] { TranscriptVersions.ShortId(p.VersionId), p.Model, p.Backend }
                .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>"2026-08-05 14:07 UTC by LocalScribe 0.9.0", or the timestamp alone when the
    /// recording build is unknown, or null when there is no timestamp at all (Tier 1 T1-8).
    /// UTC, not local: an export can cross zones between production and reading, and a bare local
    /// time in an evidentiary document is ambiguous. Null - not "" - so a renderer's `is { }`
    /// pattern omits the whole line rather than printing an empty label.</summary>
    public static string? ExportedLine(ExportProvenance p)
    {
        if (p.ExportedAtUtc is not { } at) return null;
        string stamp = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return string.IsNullOrEmpty(p.AppVersion) ? stamp : stamp + " by LocalScribe " + p.AppVersion;
    }

    /// <summary>One "Audio SHA-256 (local.flac)" label/value pair per sealed leg (Tier 1 T1-7,
    /// spec 2026-08-05 :148-153). The fabricated-silence clause is NOT optional decoration: a hash
    /// presented without it certifies machine-generated zeros as original recorded audio, which the
    /// spec calls worse than no hash at all. Composed here, once, so the .docx, .md and .txt
    /// renderers cannot word the same disclosure differently.</summary>
    public static IReadOnlyList<(string Label, string Value)> RecordedAudioLines(ExportProvenance p)
    {
        var lines = new List<(string, string)>();
        foreach (var leg in p.RecordedAudio)
        {
            string clause;
            if (leg.Silence is null)
                clause = " (machine-generated silence not recorded for this file)";
            else if (leg.Silence.SpanCount == 0)
                clause = " (no machine-generated silence)";
            else
            {
                string spans = leg.Silence.SpanCount == 1 ? "span" : "spans";
                clause = string.Create(CultureInfo.InvariantCulture,
                    $" (includes {leg.Silence.SpanCount} machine-generated silence {spans}, {Hms(leg.Silence.TotalMs)} total)");
            }
            lines.Add(("Audio SHA-256 (" + leg.FileName + ")", leg.Sha256 + clause));
        }
        return lines;
    }

    private static string Count(int n, string one, string many)
        => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");

    /// <summary>HH:MM:SS with UNBOUNDED hours. Deliberately duplicated from MaintenanceService.Hms
    /// (design 2026-08-04 section 8 review finding 1), which is private and lives in the App layer
    /// while this is Core: TimeSpan's own "hh" specifier is the Hours COMPONENT (0-23), so a
    /// 25-hour figure would silently print "01:00:00". (long)TotalHours never wraps.</summary>
    private static string Hms(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}");
    }
}
