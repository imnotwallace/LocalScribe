namespace LocalScribe.App.Services;

/// <summary>One reading of the Windows 11 call-mute tray signal (design 2026-07-11 section 2).
/// Unknown is the fail-open state: no integrated call app is reporting, the icon is absent, or
/// the text did not parse. The signal is ADVISORY - it never writes markers, never gates
/// recording (locked rule).</summary>
public enum AppMuteState { Unknown, Muted, Live }

public readonly record struct AppMuteReading(AppMuteState State, string? AppName);

/// <summary>Seam over the tray read so the watcher/VM are testable without UIA. Read() must
/// never throw - implementations fail open to Unknown.</summary>
public interface IAppMuteSignalSource
{
    AppMuteReading Read();
}
