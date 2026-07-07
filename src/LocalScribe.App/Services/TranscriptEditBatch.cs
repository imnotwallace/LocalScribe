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

/// <summary>One Edit-mode save batch from the transcript editor (design §3.4): text corrections and
/// their reverts, plus split edits and their reverts, all destined for edits.json in a single
/// <see cref="MaintenanceService.SaveTranscriptEditsAsync"/> call. Whole-section speaker pins are
/// NOT part of this batch - the editor VM calls the existing SaveSpeakerPinsAsync separately.</summary>
public sealed record TranscriptEditBatch(
    IReadOnlyDictionary<int, string> Corrections,
    IReadOnlyCollection<int> CorrectionReverts,
    IReadOnlyList<SplitEdit> Splits,
    IReadOnlyCollection<int> SplitReverts);
