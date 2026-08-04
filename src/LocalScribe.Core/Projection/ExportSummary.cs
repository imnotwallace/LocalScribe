namespace LocalScribe.Core.Projection;

/// <summary>The assistant summary block handed to an export renderer (design 2026-08-04
/// section 7). Deliberately NOT folded into ExportProvenance: a summary is CONTENT, not a fact
/// about where the transcript came from. Composed in MaintenanceService - where ProvenanceFor
/// composes, for the same reason: only the service holds both the loaded projection and the
/// export-time inputs, so the three renderers cannot disagree about staleness. The renderers
/// prepend AssistantPrompts.DraftLabel above this content; that label is locked and is never
/// carried in this record.</summary>
public sealed record ExportSummary
{
    public string ContentMarkdown { get; init; } = "";
    /// <summary>e.g. "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)".</summary>
    public string ProvenanceLine { get; init; } = "";
    /// <summary>Null when the summary is current for the rendered transcript version. Otherwise
    /// the out-of-date and/or version-mismatch notices, which renderers show in bold.</summary>
    public string? StaleNotice { get; init; }
}
