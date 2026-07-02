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
    public bool RecordingIndicator { get; init; } = true;
    public bool LaunchAtLogin { get; init; } = true;
    public LoggingSetting Logging { get; init; } = new();
    /// <summary>v3 (Stage 4, design 6.3): null until the first-run notice is accepted;
    /// detection is field-absence, not file-absence. Migration never fabricates this.</summary>
    public ConsentSetting? ConsentNotice { get; init; }
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
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
