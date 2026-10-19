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
    string ProjectedText, string RawText, bool IsCorrected, bool IsPinned,
    bool IsSplitChild = false, int PartIndex = 0,
    // A split child's own speaker override, carried verbatim from the split part (edits.json) so
    // Edit mode can pre-select the part's dropdown instead of blanking it (Stage 6.1 re-edit fix).
    // Null on unsplit segments and on split parts that carry no explicit speaker.
    string? SpeakerParticipantId = null, string? SpeakerClusterKey = null,
    // Non-null marks a HUMAN-AUTHORED line (design 2026-08-11 section 2.3), keyed by its
    // edits.json insertions id. When it is set, Seq, PartIndex, Source and RawText carry NO
    // MEANING and must not be read: there is no machine line behind this row. Seq is 0 and machine
    // seqs also START at 0 (TranscriptStore.NextSeqAsync), so any consumer that groups or looks up
    // by Seq must partition on this member FIRST or it will collide with a real segment. Source is
    // TranscriptSource.System - "neither audio stream" - which is the one value speakers.json can
    // never key, so a forgotten guard degrades to a miss rather than to a wrong pin. RawText is set
    // to the same human text as ProjectedText - there is no machine original from
    // transcript.jsonl to hold, unlike this member's summary above, so the correction dialog's
    // "both differ" contract does not apply here.
    // APPENDED LAST on purpose: this is a positional record built positionally in ~20 test files,
    // and inserting a member anywhere earlier silently rebinds every one of those arguments.
    string? InsertionId = null);
