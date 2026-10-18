// src/LocalScribe.App/Services/TranscriptEditBatch.cs
using LocalScribe.Core.Model;

namespace LocalScribe.App.Services;

/// <summary>One human-authored child of a split edit, as produced by the editor VM before it is
/// mapped onto the Core <see cref="SplitPart"/> shape (Task 9, design §3.4). Field meanings mirror
/// SplitPart exactly: Text is the child's displayed content; StartMs/DerivedStart follow the same
/// "first part inherits the machine start" contract; the speaker fields are an optional override.</summary>
public sealed record SplitPartEdit(string Text, long StartMs, bool DerivedStart,
    string? SpeakerParticipantId, string? SpeakerClusterKey);

/// <summary>One segment's split edit: which seq/source it targets and its ordered child parts.</summary>
public sealed record SplitEdit(int Seq, TranscriptSource Source, IReadOnlyList<SplitPartEdit> Parts);

/// <summary>One human-authored line in an edit batch (design 2026-08-11 section 2.3). A null
/// InsertionId MINTS a new line; a non-null one updates that existing line. Minting is deliberately
/// NOT done here: two expanded sections can each add a line in the same batch, and only
/// EditStore.ApplyInsertionsAsync - running under the per-session gate with the persisted map in
/// hand - can allocate ids that cannot collide.
/// SpeakerParticipantId is a meta.json participant id or null; never a clusterKey.</summary>
public sealed record InsertedLineEdit(string? InsertionId, string Text, long StartMs,
    string? SpeakerParticipantId);

/// <summary>One Edit-mode save batch from the transcript editor (design §3.4): text corrections and
/// their reverts, plus split edits and their reverts, all destined for edits.json in a single
/// <see cref="MaintenanceService.SaveTranscriptEditsAsync"/> call. Whole-section speaker pins are
/// NOT part of this batch - the editor VM calls the existing SaveSpeakerPinsAsync separately.</summary>
public sealed record TranscriptEditBatch(
    IReadOnlyDictionary<int, string> Corrections,
    IReadOnlyCollection<int> CorrectionReverts,
    IReadOnlyList<SplitEdit> Splits,
    IReadOnlyCollection<int> SplitReverts)
{
    /// <summary>Human-authored lines (design 2026-08-11 section 2.3) - the THIRD overlay, riding
    /// the same batch so one save is one edits.json write and one projection regeneration.
    /// A body property rather than a fifth positional parameter: a positional default must be a
    /// compile-time constant, so the only positional option is a nullable collection, and that
    /// would put a null-dereference warning in every consumer. Every existing four-argument
    /// construction keeps compiling untouched.</summary>
    public IReadOnlyList<InsertedLineEdit> Insertions { get; init; } = [];

    /// <summary>Insertion ids to REVERT (design 2026-08-11 section 2.3). Reverting a human act,
    /// the same shape as SplitReverts above - never a deletion of machine output.</summary>
    public IReadOnlyCollection<string> InsertionReverts { get; init; } = [];
}
