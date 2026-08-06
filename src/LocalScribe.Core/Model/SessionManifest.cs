namespace LocalScribe.Core.Model;

/// <summary>One run of MACHINE-GENERATED samples inside a retained audio leg (Tier 1 T1-7, spec
/// 2026-08-05 :148-153). AlignedAudioWriter zero-fills every clock gap and appends zeros to the
/// session end, and before this record nothing anywhere said where. A SHA-256 that seals the file
/// without this list certifies synthetic silence as original recorded audio - worse than no hash at
/// all, because it converts an absence of evidence into a false positive assertion.
/// Sample offsets, NOT milliseconds: the writer's arithmetic is exact in samples and a rounded ms
/// range would not identify the bytes it claims to describe. Divide by ManifestFile.SampleRate for
/// a readable time.</summary>
public sealed record FabricatedSpan
{
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    /// <summary>"clock-gap" (AlignedAudioWriter.Write filled a capture gap - a pause, a dropout or
    /// clock jitter) or "end-pad" (PadToMs appended zeros out to the stop instant so the file spans
    /// the whole session). A trailing pad and a mid-call dropout mean very different things to a
    /// reader, so they are never merged into one bucket.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>What ONE retained leg's writer fabricated, handed from SessionController to
/// ManifestBuilder at finalize (Tier 1 T1-7). Positional because it is a two-field carrier with no
/// serialization contract of its own - only its Spans reach manifest.json.</summary>
public sealed record FabricatedSilenceRecord(int SampleRate, IReadOnlyList<FabricatedSpan> Spans);

/// <summary>One sealed file inside a session folder (Tier 1 T1-7, spec 2026-08-05 :146-153).
/// Size and mtime ride along beside the hash for two reasons: they make a CHANGED verdict cheap to
/// explain to a reader, and they are what lets ManifestBuilder carry a large FLAC's hash forward
/// across an overlay write instead of re-hashing gigabytes every time a correction is saved.</summary>
public sealed record ManifestFile
{
    /// <summary>Session-folder-relative, '/'-separated - the same naming SessionArchiver uses for
    /// zip entries, so "versions/v2-.../transcript.jsonl" reads identically in both artefacts.</summary>
    public string Name { get; init; } = "";
    /// <summary>Lowercase hex (Convert.ToHexStringLower), matching ImportedSourceInfo.Sha256's
    /// documented contract so the two hashes are comparable by eye.</summary>
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    /// <summary>Retained audio legs only; 0 for text files. Divides FabricatedSilence's sample
    /// offsets into a readable time.</summary>
    public int SampleRate { get; init; }
    /// <summary>True only when the writer that PRODUCED this file reported its fabricated ranges
    /// (a live finalize), or when this entry was carried forward from such a write. False for
    /// imported audio, crash-recovered sessions and anything sealed by a build older than this
    /// feature. The distinction is the whole point: "no fabricated silence" and "we do not know"
    /// are different claims and an evidentiary artefact must never conflate them.</summary>
    public bool FabricatedSilenceKnown { get; init; }
    public IReadOnlyList<FabricatedSpan> FabricatedSilence { get; init; } = [];
}

/// <summary>manifest.json - the integrity seal over one transcript version's evidentiary files
/// (Tier 1 T1-7). Written atomically at finalize and refreshed after every overlay write and at
/// each new version. DERIVED in the sense that it can be recomputed, but never deleted as
/// housekeeping: its absence is what distinguishes an unsealed session from a tampered one.</summary>
public sealed record SessionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public string VersionId { get; init; } = TranscriptVersions.Root;
    public DateTimeOffset WrittenAtUtc { get; init; }
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];
}
