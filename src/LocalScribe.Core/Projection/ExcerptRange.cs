namespace LocalScribe.Core.Projection;

/// <summary>The millisecond window an excerpt export selects rows with (design 2026-08-04
/// section 8). Deliberately named apart from ExportProvenance.ExcerptSpan: this is the INPUT
/// window the service filters with, that is the printed label the renderers show.</summary>
public sealed record ExcerptRange(long FromMs, long ToMs);
