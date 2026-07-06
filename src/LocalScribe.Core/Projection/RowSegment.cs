using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>One constituent transcript segment of a grouped DisplayRow (Stage 6.1). Restores the
/// per-seq identity SectionGrouper's text concatenation otherwise drops, so the read view can key
/// corrections (edits.json) and pins (speakers.json) off grouped rows. ProjectedText is the
/// displayed text (post vocabulary + edits); RawText is the machine original from transcript.jsonl
/// - the correction dialog shows both because a correction is stored verbatim and must never
/// silently bake the vocabulary pass into an evidentiary edit without the user seeing the original.</summary>
public sealed record RowSegment(
    int Seq, TranscriptSource Source, long StartMs, long EndMs,
    string ProjectedText, string RawText, bool IsCorrected, bool IsPinned);
