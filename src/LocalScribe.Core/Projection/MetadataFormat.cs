using System.Globalization;
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
}
