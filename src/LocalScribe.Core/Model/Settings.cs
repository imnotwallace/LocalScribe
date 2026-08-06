// src/LocalScribe.Core/Model/Settings.cs
namespace LocalScribe.Core.Model;

/// <summary>settings.json (spec section 7, schema v3), in %APPDATA%/LocalScribe.</summary>
public sealed record Settings
{
    public int SchemaVersion { get; init; } = 3;
    public string StorageRoot { get; init; } = "%USERPROFILE%/LocalScribe";
    public string AudioRetention { get; init; } = "keep";
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Flac;
    public SelfIdentity Self { get; init; } = new();
    public string Model { get; init; } = "auto";
    public Backend Backend { get; init; } = Backend.Auto;
    public string Language { get; init; } = "auto";
    public RemoteSetting Remote { get; init; } = new();
    public MicSetting Mic { get; init; } = new();
    public AutoDetectSetting AutoDetect { get; init; } = new();
    public OverlaySetting Overlay { get; init; } = new();
    public Vocabulary Vocabulary { get; init; } = new();
    public HotkeysSetting Hotkeys { get; init; } = new();
    public string Timestamps { get; init; } = "relative";
    /// <summary>v3 (Stage 5.4, design 4.2): a same-speaker silence gap at/above this many
    /// milliseconds starts a new transcript section in both live and read views (display-only;
    /// transcript.jsonl is never mutated). Additive - existing v3 files without it load at this
    /// default, so no schema bump/migration is required.</summary>
    public int SectionGapMs { get; init; } = 5000;
    public bool RecordingIndicator { get; init; } = true;
    public bool LaunchAtLogin { get; init; } = true;
    public LoggingSetting Logging { get; init; } = new();
    /// <summary>v3 (Stage 4, design 6.3): null until the first-run notice is accepted;
    /// detection is field-absence, not file-absence. Migration never fabricates this.</summary>
    public ConsentSetting? ConsentNotice { get; init; }
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
    /// <summary>v3 (Steno round, design 2026-07-18 section 7): local assistant. Additive -
    /// existing v3 files without it load at this default, so no schema bump / migration is
    /// required (the SectionGapMs precedent).</summary>
    public AssistantSetting Assistant { get; init; } = new();
    /// <summary>v3 (design 2026-07-18 section 5.2): the call-detection advisory's master toggle +
    /// exe allowlist. Additive - existing v3 files without it load at this default (the
    /// SectionGapMs precedent), so no schema bump/migration is required. Default ON is safe by the
    /// locked rule: detection is ADVISORY-ONLY (an offer toast) - it never starts/stops/pauses
    /// capture and never writes markers. Distinct from the dormant AutoDetectSetting above (a
    /// disabled v1 seam pinned off by the migration tests, friendly-name-shaped) - that record is
    /// deliberately left untouched.</summary>
    public CallDetectSetting CallDetect { get; init; } = new();
    /// <summary>v3 (Steno round, design 2026-07-18 section 6): Record-console behavior. Additive -
    /// existing v3 files without it load at the defaults, so no schema bump/migration is required
    /// (the SectionGapMs precedent).</summary>
    public ConsoleSetting Console { get; init; } = new();
    /// <summary>v3 (semantic search, design 2026-07-25): master toggle for the Related-discussion
    /// semantic section + its background embedding indexer. Additive - existing v3 files without
    /// it load at this default (the SectionGapMs precedent), so no schema bump/migration. The
    /// feature is additionally presence-gated: helper + embedding-role model must exist.</summary>
    public SemanticSearchSetting SemanticSearch { get; init; } = new();
    /// <summary>v3 (design 2026-08-04): remembered export choices + export knobs. Additive -
    /// existing v3 files without it load at the defaults (the SectionGapMs precedent).</summary>
    public ExportSetting Export { get; init; } = new();
}

public sealed record SelfIdentity { public string Name { get; init; } = ""; public string? Role { get; init; } }
public sealed record RemoteSetting { public RemoteMode Mode { get; init; } = RemoteMode.Auto; public string? App { get; init; } }
public sealed record MicSetting { public MicMode Mode { get; init; } = MicMode.FollowDefault; public string? Id { get; init; } public string? Name { get; init; } }
public sealed record AutoDetectSetting { public bool Enabled { get; init; } public IReadOnlyList<string> Apps { get; init; } = ["Teams", "Zoom", "Webex"]; }
public sealed record OverlaySetting { public bool Enabled { get; init; } = true; public bool ShowSessionName { get; init; } public bool ShowLevelMeter { get; init; } = true; public bool ExcludeFromCapture { get; init; } = true; }
public sealed record HotkeysSetting { public string StartStop { get; init; } = "Ctrl+Alt+R"; public string Pause { get; init; } = "Ctrl+Alt+P"; }
public sealed record LoggingSetting { public string Level { get; init; } = "info"; public bool IncludeTranscriptText { get; init; } }
public sealed record ConsentSetting { public DateTimeOffset AcknowledgedAtUtc { get; init; } public string AppVersion { get; init; } = ""; }
public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }
/// <summary>Model is a manifest canonical name; null = the locked default
/// (Qwen3-4B-Instruct-2507). Enabled=false hides/disables all assistant UI.</summary>
public sealed record AssistantSetting { public bool Enabled { get; init; } = true; public string? Model { get; init; } }
/// <summary>Call-detection advisory config (design 2026-07-18 section 5.2). Apps hold exe-file
/// spellings ("webex.exe") for readability; matching strips the extension and ignores case
/// (CallDetectionPolicy.ExeKey, Task 3) because WASAPI session images arrive EXTENSIONLESS
/// (Process.ProcessName). Browsers are excluded by default (addable). The real Webex
/// capture-session owner exe is verified during smoke and these defaults adjusted if it differs
/// (Global Constraints).</summary>
public sealed record CallDetectSetting
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Apps { get; init; } =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];
}
/// <summary>Record-console options (design 2026-07-18 section 6). CompactOnStart: collapse the
/// console to the compact always-on-top pill when recording starts - DEFAULT OFF (opt-in).</summary>
public sealed record ConsoleSetting { public bool CompactOnStart { get; init; } }
public sealed record SemanticSearchSetting { public bool Enabled { get; init; } = true; }

/// <summary>Remembered export choices + the export knobs (design 2026-08-04 sections 4-7).
/// Additive - existing v3 files without it load at these defaults (the SectionGapMs precedent),
/// so no schema bump/migration is required. Every default reproduces the pre-Round-2 behaviour
/// exactly. The excerpt range is deliberately NOT here: a remembered range would silently emit a
/// partial export of the next, unrelated session (design section 8).</summary>
public sealed record ExportSetting
{
    public ExportFormat Format { get; init; } = ExportFormat.Zip;
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    public bool ExtraTimestamps { get; init; }
    /// <summary>Extra-timestamp cadence. The dialog offers 10/15/30/60 s; a hand-typed value in
    /// settings.json is kept as the effective value rather than rewritten (design section 5).</summary>
    public int CadenceIntervalMs { get; init; } = 15000;
    /// <summary>Save-As default-name template. Tokens: {title} {date} {time} {matter} {version}
    /// {id}. Applies to the three TEXTUAL formats; the .zip keeps its session-id name.</summary>
    public string FilenameTemplate { get; init; } = "{title}";
    /// <summary>Attach the latest assistant summary. Default OFF: the export is the document that
    /// leaves the building, so attaching a machine-written draft must be an act (design 7).</summary>
    public bool IncludeSummary { get; init; }
    /// <summary>Flag rewritten turns in the exported document (Tier 1 T1-8). Additive - existing
    /// v3 files without it load at this default (the SectionGapMs precedent), so no schema
    /// bump/migration is required. Default ON: see ExportOptions.MarkCorrectedTurns.</summary>
    public bool MarkCorrectedTurns { get; init; } = true;
}
