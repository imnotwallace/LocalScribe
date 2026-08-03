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

    /// <summary>"v2 \u00B7 large-v3-turbo \u00B7 cuda". Rendered for originals too -
    /// ShortId("v1") returns "v1", so no special-casing (design 2026-08-03 section 6).</summary>
    public static string VersionLine(ExportProvenance p)
        => string.Join(" \u00B7 ",
            new[] { TranscriptVersions.ShortId(p.VersionId), p.Model, p.Backend }
                .Where(s => !string.IsNullOrEmpty(s)));
}
