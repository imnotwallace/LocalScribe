using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>session.json - system-owned truth (spec section 1.2, schema v4). No user-editable fields
/// (those live in meta.json). Rewritten on finalize/relabel/recovery.</summary>
public sealed record SessionRecord
{
    public int SchemaVersion { get; init; } = 4;
    public string Id { get; init; } = "";
    public AppKind App { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>Windows time-zone ID + DST-resolved UTC offset in force at Start (spec section 1.2).
    /// Local display time = StartedAtUtc + UtcOffsetMinutes; both null for pre-v3 records
    /// (renderers then fall back to the machine's current zone).</summary>
    public string? TimeZoneId { get; init; }
    public int? UtcOffsetMinutes { get; init; }

    public long DurationMs { get; init; }
    public IReadOnlyList<SourceKind> Sources { get; init; } = [];
    public string Model { get; init; } = "";
    /// <summary>The exact ggml weights file that produced the (last) transcription - Model alone
    /// no longer determines it (ModelFileResolver picks quantized variants per backend). Null
    /// means UNKNOWN or none: pre-existing records, sessions that never transcribed a segment,
    /// and crash-RECOVERED sessions (the value is only persisted at finalize, so a crash loses
    /// it even when segments exist). Mid-session changes additionally leave "transcription
    /// weights changed" markers in the transcript.</summary>
    public string? WeightsFile { get; init; }
    public string Backend { get; init; } = "";
    public string Language { get; init; } = "";
    public IReadOnlyList<SourceKind> RetainedAudioSources { get; init; } = [];
    public bool Diarised { get; init; }
    public int SegmentCount { get; init; }
    public int MarkerCount { get; init; }
    public bool Recovered { get; init; }
    public string AppVersion { get; init; } = "";
    public DeviceSnapshot Devices { get; init; } = new();

    /// <summary>Which transcript the app reads/edits/exports (design 2026-07-13 section 3):
    /// "v1" = the immutable session-root original; otherwise a TranscriptVersion.Id resolving to
    /// versions\&lt;id&gt;\. Root truth fields above (Model/Backend/Language/SegmentCount/...)
    /// always describe the ORIGINAL v1 run - per-version actuals live in the Versions entries.</summary>
    public string ActiveVersion { get; init; } = "v1";

    /// <summary>Completed re-transcriptions, oldest first (v2, v3, ...). The root v1 has no
    /// entry here. An entry is written in the SAME session.json save that flips ActiveVersion
    /// (the run's single commit point), so a listed version is always a complete folder.</summary>
    public IReadOnlyList<TranscriptVersion> Versions { get; init; } = [];

    /// <summary>How this session came to exist (design 2026-07-13 section 4.1): "recorded" (the
    /// default - absent in every pre-existing session.json, so old files load unchanged) or
    /// "imported" (created by AudioImporter from a received file). Additive, no schema bump -
    /// the MicSnapshot.FellBackToDefault precedent.</summary>
    public string Origin { get; init; } = "recorded";

    /// <summary>Chain-of-custody metadata for an imported session's original file; null (and
    /// omitted on disk via WhenWritingNull) for recorded sessions.</summary>
    public ImportedSourceInfo? ImportedSource { get; init; }
}

/// <summary>Provenance of an imported session's original file (design 2026-07-13 section 4.1).
/// The original bytes live unmodified at source\{FileName}; Sha256 is computed over those bytes
/// at copy time. Claimed* fields are CONTAINER claims (ffprobe / WAV header); Decoded* fields are
/// decoded-stream truth (the verified Meetily bug class: never trust container headers).
/// DurationMismatch records that the >1 percent gate fired and the user chose Continue (the
/// transcript also carries Markers.ImportedDurationMismatch).</summary>
public sealed record ImportedSourceInfo
{
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";              // lowercase hex over the original bytes
    public long FileSizeBytes { get; init; }
    public string ContainerFormat { get; init; } = "";     // ffprobe format_name, e.g. "mp3"
    public DateTimeOffset? FileCreatedUtc { get; init; }
    public DateTimeOffset? FileModifiedUtc { get; init; }
    public DateTimeOffset? MediaCreatedUtc { get; init; }  // container media-creation tag, if any
    public long? ClaimedDurationMs { get; init; }          // null when the container states none
    public long DecodedDurationMs { get; init; }
    public int DecodedSampleRate { get; init; }
    public int DecodedChannels { get; init; }
    public string ChannelMapping { get; init; } = "";      // mono | split | split-swapped | downmix | downmix-multichannel
    public bool DurationMismatch { get; init; }
}

/// <summary>Resolved device actuals captured at Start (spec section 1.2/section 12).</summary>
public sealed record DeviceSnapshot
{
    public MicSnapshot Mic { get; init; } = new();
    public RemoteSnapshot Remote { get; init; } = new();
}

public sealed record MicSnapshot
{
    public MicMode Mode { get; init; } = MicMode.FollowDefault;
    public string? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>True when Mode was Pinned but the pinned device was absent at Start, so capture
    /// fell back to the Communications default (design section 2). Drives the spec section 12
    /// "pinned microphone unavailable -> default" marker. Additive (defaults false): pre-existing
    /// v3 session.json files load unchanged - no schema bump. Mirrors
    /// RemoteSnapshot.FellBackToSystemMix.</summary>
    public bool FellBackToDefault { get; init; }
}

public sealed record RemoteSnapshot
{
    public RemoteMode Mode { get; init; } = RemoteMode.Auto;
    public string? App { get; init; }
    public bool FellBackToSystemMix { get; init; }
}

/// <summary>One completed re-transcription (design 2026-07-13 section 3.1). Id is the FULL
/// version-folder name under versions\ ("v2-base.en-2026-07-13") and doubles as the
/// SessionRecord.ActiveVersion value, so StoragePaths.VersionDir stays a pure join.</summary>
public sealed record TranscriptVersion
{
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>The exact ggml weights file that produced this version (mirrors
    /// SessionRecord.WeightsFile - Model alone no longer determines the file; ModelFileResolver
    /// picks quantized variants per backend). Null = no segment was ever transcribed.</summary>
    public string? WeightsFile { get; init; }
    public string Backend { get; init; } = "";
    public string Language { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>True when the run's Whisper initial prompt carried global/matter vocabulary
    /// terms (design 2026-07-13 section 3.2).</summary>
    public bool VocabularyApplied { get; init; }
}

/// <summary>Pure helpers over version ids. A static class (not computed properties on
/// TranscriptVersion) so nothing extra serializes into session.json.</summary>
public static class TranscriptVersions
{
    /// <summary>The session-root pseudo-version: never has a folder or a Versions entry.</summary>
    public const string Root = "v1";

    /// <summary>"v2-base.en-2026-07-13" -> "v2" (badge/footer display form).</summary>
    public static string ShortId(string versionId)
    {
        int i = versionId.IndexOf('-');
        return i < 0 ? versionId : versionId[..i];
    }

    /// <summary>The monotonic number inside a version id; 1 for "v1" or anything unparseable
    /// (an unparseable folder name then never blocks NewId's max+1 numbering).</summary>
    public static int Number(string versionId)
    {
        string shortId = ShortId(versionId);
        return shortId.Length > 1 && shortId[0] == 'v'
            && int.TryParse(shortId.AsSpan(1), out int n) ? n : 1;
    }

    public static string NewId(int number, string model, DateOnly date)
        => $"v{number}-{model}-{date:yyyy-MM-dd}";
}
