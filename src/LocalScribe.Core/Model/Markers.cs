namespace LocalScribe.Core.Model;

/// <summary>Canonical in-transcript marker messages (spec section 8.1). The arrow in
/// <see cref="PinnedMicUnavailable"/> is written as a \u escape so this source file stays ASCII;
/// the rendered string is the spec glyph. Only RecoveredSession is emitted in Stage 2a (recovery);
/// the rest are raised by Stage 2b / Stage 7 and shared from here.</summary>
public static class Markers
{
    public const string AudioDeviceChanged = "audio device changed";
    public const string PausedSystemSleep = "paused: system sleep";
    public const string Resumed = "resumed";
    public const string PausedByUser = "paused by user";
    public const string DegradedSystemAudioLoopback = "degraded: system-audio loopback";
    public const string PinnedMicUnavailable = "pinned microphone unavailable \u2192 default";
    public const string TranscriptionLagging = "transcription lagging";
    public const string RecoveredSession = "recovered session";
    public const string TranscriptionFailed = "transcription failed";
    public const string LocalMuted = "microphone muted by user";
    public const string LocalUnmuted = "microphone unmuted";
    public const string MicDeviceMuted = "microphone device muted";
    public const string MicDeviceUnmuted = "microphone device unmuted";

    // Capture Scope Control (design 2026-07-12 section 3). "by user" marks these as DELIBERATE
    // live switches (parallel to PausedByUser / LocalMuted), distinguishing them from the
    // involuntary DegradedSystemAudioLoopback that the per-app->system-mix fallback reuses.
    public const string RemoteCaptureChangedSystemMix = "remote capture changed to full system mix by user (all machine audio)";
    public const string RemoteCaptureChangedPerApp    = "remote capture changed to per-app by user: {0}";
}
