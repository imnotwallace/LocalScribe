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
    /// <summary>v3 (Stage 6.3, design 3.3): the per-page footer string stamped into exported .docx
    /// transcripts (spec 11.2). Additive - existing v3 files without it load at this default, so no
    /// schema bump / migration is required (the SectionGapMs precedent). Read-only elsewhere.</summary>
    public string DocxFooterText { get; init; } = "PRIVILEGED & CONFIDENTIAL";
    public bool RecordingIndicator { get; init; } = true;
    public bool LaunchAtLogin { get; init; } = true;
    public LoggingSetting Logging { get; init; } = new();
    /// <summary>v3 (Stage 4, design 6.3): null until the first-run notice is accepted;
    /// detection is field-absence, not file-absence. Migration never fabricates this.</summary>
    public ConsentSetting? ConsentNotice { get; init; }
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
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
