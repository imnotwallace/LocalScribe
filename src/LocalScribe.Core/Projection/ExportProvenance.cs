using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Export-only provenance for a rendered transcript (design 2026-08-03 section 1).
/// Deliberately NOT folded into SessionTextView: that record is the neutral, app-independent
/// metadata projection behind session.txt and must not grow export-specific fields. Composed in
/// MaintenanceService (where the old footerText composed), so both renderers stay pure
/// serializers. House style mirrors ExportOptions: sealed record + { get; init; } with inline
/// defaults.</summary>
/// <summary>How much of a retained leg is machine-generated (Tier 1 T1-7, spec 2026-08-05
/// :148-153), summarised from the manifest's FabricatedSpan list for a reader who is not going to
/// open manifest.json. NULL means "not recorded" - a distinct claim from a zero count, and the two
/// must never be conflated in an evidentiary document.</summary>
public sealed record FabricatedSilenceSummary(int SpanCount, long TotalMs);

/// <summary>One retained audio leg's seal, read from manifest.json at export time (Tier 1 T1-7).
/// This does NOT re-open the 2026-08-04 ruling against hashing recorded audio AT EXPORT TIME: the
/// hash was computed once at finalize and this is a small JSON read of the stored value. No audio
/// file is opened on the export path.</summary>
public sealed record RecordedAudioLeg
{
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public FabricatedSilenceSummary? Silence { get; init; }
}

/// <summary>What a PERSON changed after the machine produced this transcript, plus what the render
/// layer removed (Tier 1 T1-8, spec 2026-08-05 :161-166). Five separate counts, not one total,
/// because each maps to exactly one on-disk structure and a reader asking "was this rewritten?"
/// wants a different answer than one asking "was anything left out?".
/// Corrections and Splits are counted from edits.json SEPARATELY: a split's parts are emitted with
/// Corrected=false (TranscriptProjection), so counting ProjectedSegment.Corrected alone
/// undercounts the human layer. SpeakerPins and SpeakerNames come from speakers.json, which
/// edits.json knows nothing about.</summary>
public sealed record HumanLayerCounts
{
    public int Corrections { get; init; }
    public int Splits { get; init; }
    /// <summary>Segments a human pinned to a specific speaker (speakers.json Pinned, summed across
    /// sources) - NOT diarisation's own Assignments, which are machine output.</summary>
    public int SpeakerPins { get; init; }
    /// <summary>Clusters a human gave a name to (speakers.json Names).</summary>
    public int SpeakerNames { get; init; }
    /// <summary>Segments PhantomBleedDedup removed from every visible surface, this document
    /// included. The one count here that is not a human act, and the one whose absence reads as
    /// concealment.</summary>
    public int SuppressedDuplicates { get; init; }
}

public sealed record ExportProvenance
{
    public string VersionId { get; init; } = TranscriptVersions.Root;

    /// <summary>The session folder id (Tier 1 T1-8, spec 2026-08-05 :161-166). Without it a .docx
    /// served on the other side cannot be tied back to the record it was rendered from - the title
    /// is user-editable and several sessions may share one. "" for an all-default instance, which
    /// keeps pre-feature output byte-identical.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>When this document was produced, from the injected TimeProvider - never
    /// DateTime.UtcNow (Tier 1 T1-8). Rendered in UTC beside AppVersion, because "which build made
    /// this" and "when" are one question in practice. Null for an all-default instance.</summary>
    public DateTimeOffset? ExportedAtUtc { get; init; }

    /// <summary>SessionRecord.AppVersion - the build that RECORDED the session, not the one
    /// exporting it. Those differ whenever an old session is re-exported, and the recording build
    /// is the evidentiary fact (Tier 1 T1-8).</summary>
    public string AppVersion { get; init; } = "";

    /// <summary>The exact ggml file that produced this transcript version, e.g.
    /// "ggml-small.en-q8_0.bin" (Tier 1 T1-8). Model alone no longer determines it -
    /// ModelFileResolver picks quantized variants per backend. Null for crash-recovered sessions
    /// and for sessions that never transcribed a segment, where the renderers omit the line rather
    /// than print an empty one.</summary>
    public string? WeightsFile { get; init; }

    public string Model { get; init; } = "";
    public string Backend { get; init; } = "";
    /// <summary>Imported sessions only, from ImportedSourceInfo. Null for recorded sessions -
    /// hashing recorded audio AT EXPORT TIME is deliberately out of scope (it would hash a large
    /// FLAC on every export), and that 2026-08-04 ruling STANDS. A recorded session's audio hash
    /// arrives on RecordedAudio instead, computed once at finalize (Tier 1 T1-7) and merely READ
    /// here - no audio file is opened on the export path.</summary>
    public string? AudioFileName { get; init; }
    public string? AudioSha256 { get; init; }

    /// <summary>The catalog subtitle for Model, e.g. "Decent accuracy, English only - quick"
    /// (Tier 1 T1-6, spec 2026-08-05 :66-72). The owner ruled the live model cap stays, so the
    /// divergence from import's large-v3-turbo default is DISCLOSED here. "" for an uncatalogued
    /// model and for an all-default instance, which keeps pre-feature output byte-identical.</summary>
    public string ModelAccuracy { get; init; } = "";

    /// <summary>SHA-256 of the transcript.jsonl this document was rendered from, read from
    /// manifest.json (Tier 1 T1-7). Null when the session has no seal - every session recorded
    /// before manifests existed.</summary>
    public string? TranscriptSha256 { get; init; }

    /// <summary>Each retained leg's seal (Tier 1 T1-7). Empty for an imported session, whose audio
    /// provenance is the AudioFileName/AudioSha256 pair above, and for an unsealed one.</summary>
    public IReadOnlyList<RecordedAudioLeg> RecordedAudio { get; init; } = [];

    /// <summary>Null renders NO line, which is what an all-default instance (and therefore every
    /// pre-feature golden test) produces. ProvenanceFor always supplies it for a real export, so a
    /// genuinely untouched transcript still gets "Human edits: none" - a positive statement, not
    /// silence (Tier 1 T1-8).</summary>
    public HumanLayerCounts? HumanLayer { get; init; }
    /// <summary>The session has no EndedAtUtc - exported mid-recording, so the transcript is
    /// incomplete and diarisation has not run (design 2026-08-03 section 11).</summary>
    public bool InProgress { get; init; }
    /// <summary>Non-null when this document is a TIME-RANGE EXCERPT (design 2026-08-04 section 8):
    /// the ACTUAL span the selected rows cover, e.g. "00:12:30-00:18:45 of 01:47:12" - snapped
    /// outward to whole turns, never the requested range. A fact about the document's
    /// completeness, the same category as InProgress, which is why it lives beside it. The INPUT
    /// window is ExcerptRange; renderers never see it and never filter rows.</summary>
    public string? ExcerptSpan { get; init; }
}
