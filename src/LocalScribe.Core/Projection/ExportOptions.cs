namespace LocalScribe.Core.Projection;

/// <summary>The user-facing export toggles (design 3.3 + 2026-08-02 item 5; renamed from
/// DocxOptions in design 2026-08-04 section 2, where a fourth renderer made the old name plainly
/// wrong). House style mirrors PhantomBleedOptions: sealed record + { get; init; } with inline
/// defaults. Format-neutral and shared by the .docx, .md and .txt export renderers.</summary>
public sealed record ExportOptions
{
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    /// <summary>Extra mid-turn stamp cadence (design 2026-08-02 item 5): a named "(cont'd)"
    /// continuation paragraph starts at the first segment boundary at/after this many ms since the
    /// last shown stamp. 0 (default) = off. Renderers force it off when IncludeTimestamps is
    /// false. Independent of - and additional to - the always-on ContinuationMaxChars trigger
    /// (design 2026-08-03 section 8).</summary>
    public int TimestampIntervalMs { get; init; } = 0;
    /// <summary>Attach the latest assistant summary (design 2026-08-04 section 7). Default OFF:
    /// the export is the document that leaves the building, so attaching a machine-written draft
    /// must be an act, not a default.</summary>
    public bool IncludeSummary { get; init; }
}
