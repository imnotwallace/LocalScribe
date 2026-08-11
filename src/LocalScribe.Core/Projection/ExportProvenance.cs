namespace LocalScribe.Core.Projection;

/// <summary>What a PERSON changed after the machine produced this transcript, plus what the render
/// layer removed (Tier 1 T1-8). Five separate counts, not one total, because each maps to exactly
/// one on-disk structure.
/// Corrections and Splits are counted from edits.json SEPARATELY: a split's parts are emitted with
/// Corrected=false (TranscriptProjection), so counting ProjectedSegment.Corrected alone
/// undercounts the human layer. SpeakerPins and SpeakerNames come from speakers.json, which
/// edits.json knows nothing about.
///
/// The exported header stopped PRINTING these counts in Part 3 (design 2026-08-11 section 3.3),
/// but the record survives because two things read it: MetadataFormat.DisclaimerFor picks the
/// disclaimer variant from the four HUMAN counts, and MetadataFormat.OmittedLine discloses
/// SuppressedDuplicates.</summary>
public sealed record HumanLayerCounts
{
    public int Corrections { get; init; }
    public int Splits { get; init; }
    /// <summary>Segments a human pinned to a specific speaker (speakers.json Pinned, summed across
    /// sources) - NOT diarisation's own Assignments, which are machine output.</summary>
    public int SpeakerPins { get; init; }
    /// <summary>Clusters a PERSON gave a name to - speakers.json Names entries whose value DIFFERS
    /// from the label the diariser would have generated for that clusterKey
    /// (DefaultSpeakerLabels.IsMachineDefault). Not Names.Count: SpeakerDetectionStep writes
    /// DefaultSpeakerLabels.For(...) into that map for every cluster an auto-detected import finds,
    /// with no user interaction at all, so counting the raw entries let a wholly automatic import
    /// select the "reviewed and corrected by a person" disclaimer for a document nobody had ever
    /// opened (whole-branch review finding 1). Keep this meaning if the count ever moves: it is the
    /// only member here whose source data is written by BOTH a machine and a human.</summary>
    public int SpeakerNames { get; init; }
    /// <summary>Segments PhantomBleedDedup removed from every visible surface, this document
    /// included. The one count here that is not a human act, and the one whose absence reads as
    /// concealment - which is why it is the one that still prints, on the Omitted line.</summary>
    public int SuppressedDuplicates { get; init; }
}

/// <summary>Export-only provenance for a rendered transcript (design 2026-08-03 section 1).
/// Deliberately NOT folded into SessionTextView: that record is the neutral, app-independent
/// metadata projection behind session.txt and must not grow export-specific fields. Composed in
/// MaintenanceService.ProvenanceFor, so the renderers stay pure serializers.
///
/// Part 3 (design 2026-08-11 section 3.3) cut this from fourteen members to four. The removed
/// facts - app version, session id, export instant, transcript version, model, backend, weights
/// file, model accuracy, and every SHA-256 - were rendered ONLY into the exported header, and the
/// header stopped carrying them. They are not lost: the .zip bundle is a verbatim folder copy
/// (SessionArchiver.AddSessionFolderAsync), so session.json, meta.json and manifest.json still
/// carry all of them, and Verify integrity is untouched. The evidentiary archive keeps the full
/// record; the reading copy is lean. Do NOT re-add a field here without a renderer that prints
/// it - a write-only property reads as a promise the document does not keep.</summary>
public sealed record ExportProvenance
{
    /// <summary>Imported sessions only, from ImportedSourceInfo - the original file the audio came
    /// from, rendered as the "Source" line. Null for recorded sessions, whose legs are the
    /// hardcoded local.flac / remote.flac (StoragePaths.AudioFile) and therefore name nothing a
    /// reader could use; they get no Source line at all (owner decision 2026-08-11).</summary>
    public string? AudioFileName { get; init; }

    /// <summary>Null renders the UNTOUCHED disclaimer and no Omitted line, which is what an
    /// all-default instance (and therefore every renderer unit test) produces. ProvenanceFor
    /// always supplies it for a real export.</summary>
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
