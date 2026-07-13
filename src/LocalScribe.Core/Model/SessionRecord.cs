using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>session.json - system-owned truth (spec section 1.2, schema v3). No user-editable fields
/// (those live in meta.json). Rewritten on finalize/relabel/recovery.</summary>
public sealed record SessionRecord
{
    public int SchemaVersion { get; init; } = 3;
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
    /// no longer determines it (ModelFileResolver picks quantized variants per backend). Null on
    /// pre-existing records and sessions that never transcribed a segment. Mid-session changes
    /// additionally leave "transcription weights changed" markers in the transcript.</summary>
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
