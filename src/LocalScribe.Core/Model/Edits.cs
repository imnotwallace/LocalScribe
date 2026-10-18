// src/LocalScribe.Core/Model/Edits.cs
namespace LocalScribe.Core.Model;

/// <summary>A single in-place text correction, keyed by the immutable seq (spec section 1.6).</summary>
public sealed record Correction
{
    public string Text { get; init; } = "";
    public DateTimeOffset EditedAtUtc { get; init; }
}

/// <summary>One human-authored child of a split segment (design §2.1). Text is the child's
/// displayed content; StartMs is its start (full ms). DerivedStart is false only for the first
/// part, which inherits the machine segment's start; later parts carry a human estimate. Speaker
/// is an OPTIONAL override resolved at projection: at most one of SpeakerParticipantId /
/// SpeakerClusterKey is set; null on both means the child inherits the parent seq's resolved
/// name. Stored in the split entry, NOT speakers.json, so speakers.json stays integer-seq keyed.</summary>
public sealed record SplitPart
{
    public string Text { get; init; } = "";
    public long StartMs { get; init; }
    public bool DerivedStart { get; init; }
    public string? SpeakerParticipantId { get; init; }
    public string? SpeakerClusterKey { get; init; }
}

/// <summary>A non-destructive split overlay for one machine segment (design §2). Partitions the
/// original into Parts (>= 2, display order); the machine transcript.jsonl line is untouched and
/// revert = removing this entry.</summary>
public sealed record SplitEntry
{
    public TranscriptSource Source { get; init; }
    public DateTimeOffset EditedAtUtc { get; init; }
    public IReadOnlyList<SplitPart> Parts { get; init; } = [];
}

/// <summary>One transcript line a PERSON authored where the machine produced nothing (design
/// 2026-08-11 section 2.3): an inaudible passage, a speaker off-mic, a turn the ASR dropped
/// entirely. Stored in edits.json keyed by its own generated id, NEVER in transcript.jsonl - the
/// machine record stays a pure record of what the machine produced, so SegmentCount + MarkerCount
/// arithmetic (six sites) and the TranscriptKind enum are both untouched.
///
/// StartMs is human-estimated BY DEFINITION and gets the same treatment as SplitPart.DerivedStart:
/// it must fall within the session duration but need NOT fall inside any machine segment, which is
/// the whole point - the machine found no speech there.
///
/// SpeakerParticipantId is a meta.json participant id, or null for an unattributed line. It is
/// DELIBERATELY NOT a clusterKey: speakers.json maps DIARISATION CLUSTERS to names, and a
/// human-authored line has no cluster, so routing this through NameResolver's cluster tiers would
/// be a category error and would collide two key spaces that must stay apart.</summary>
public sealed record InsertedLine
{
    public string Text { get; init; } = "";
    public long StartMs { get; init; }
    public string? SpeakerParticipantId { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; }
}

/// <summary>edits.json - non-destructive text-correction overlay (spec section 1.6). No tombstones/
/// hide/delete. Speaker corrections live in speakers.json, not here.</summary>
public sealed record Edits
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, Correction> Corrections { get; init; } = new Dictionary<string, Correction>();
    public IReadOnlyDictionary<string, SplitEntry> Splits { get; init; } = new Dictionary<string, SplitEntry>();

    /// <summary>Human-authored lines, keyed by generated id (design 2026-08-11 section 2.3). The
    /// THIRD human overlay, alongside Corrections and Splits. Present only when non-empty, which is
    /// also what gates the schema version - see EditStore.WriteVersion.</summary>
    public IReadOnlyDictionary<string, InsertedLine> Insertions { get; init; }
        = new Dictionary<string, InsertedLine>();

    /// <summary>The highest insertion ordinal ever minted for this version, so a REVERTED id is
    /// never handed to a different human act (Task 2 review, 2026-08-11). Not derivable from
    /// Insertions: a revert removes the entry outright - this file has no tombstones by design -
    /// so minting from the surviving keys alone resurrects the revoked id, and manifest.json
    /// discloses inserted lines BY ID, so two exports of one session could carry two different
    /// human acts under one identity. Zero on a file written before insertions existed, which is
    /// correct: nothing has been minted. Additive and skippable, so it does NOT raise
    /// WriteVersion - a file with a high-water mark but no insertions has nothing an older build
    /// could drop.</summary>
    public int HighestInsertionOrdinal { get; init; }
}
