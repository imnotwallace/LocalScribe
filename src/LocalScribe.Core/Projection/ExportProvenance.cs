using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Export-only provenance for a rendered transcript (design 2026-08-03 section 1).
/// Deliberately NOT folded into SessionTextView: that record is the neutral, app-independent
/// metadata projection behind session.txt and must not grow export-specific fields. Composed in
/// MaintenanceService (where the old footerText composed), so both renderers stay pure
/// serializers. House style mirrors ExportOptions: sealed record + { get; init; } with inline
/// defaults.</summary>
public sealed record ExportProvenance
{
    public string VersionId { get; init; } = TranscriptVersions.Root;
    public string Model { get; init; } = "";
    public string Backend { get; init; } = "";
    /// <summary>Imported sessions only, from ImportedSourceInfo. Null for recorded sessions -
    /// hashing recorded audio at export time is deliberately out of scope (it would hash a large
    /// FLAC on every export).</summary>
    public string? AudioFileName { get; init; }
    public string? AudioSha256 { get; init; }
    /// <summary>The session has no EndedAtUtc - exported mid-recording, so the transcript is
    /// incomplete and diarisation has not run (design 2026-08-03 section 11).</summary>
    public bool InProgress { get; init; }
}
