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

/// <summary>edits.json - non-destructive text-correction overlay (spec section 1.6). No tombstones/
/// hide/delete. Speaker corrections live in speakers.json, not here.</summary>
public sealed record Edits
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, Correction> Corrections { get; init; } = new Dictionary<string, Correction>();
    public IReadOnlyDictionary<string, SplitEntry> Splits { get; init; } = new Dictionary<string, SplitEntry>();
}
